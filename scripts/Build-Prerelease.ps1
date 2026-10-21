<#
.SYNOPSIS
Builds a prerelease of the NuGet package into ./dist, and optionally installs or pushes it.

.DESCRIPTION
The version is the <VersionPrefix> of Directory.Build.props with a suffix: 0.1.0-preview.20260101120000. The package is packed with
`dotnet pack --version-suffix`.

.PARAMETER Suffix
The prerelease label. Default: preview.<UTC date and time>, which sorts after earlier ones.

.PARAMETER Output
Where the package goes. Default: dist, next to this repository's solution file.

.PARAMETER SkipTests
Do not run the tests first.

.PARAMETER Push
Also push the package to GitHub Packages of this owner (needs a token that can write packages, in the environment variable GITHUB_TOKEN).

.PARAMETER Owner
The GitHub user or organization to push to. Default: joost-morsink.

.EXAMPLE
./scripts/Build-Prerelease.ps1

.EXAMPLE
./scripts/Build-Prerelease.ps1 -Suffix rc.1 -SkipTests
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*$')]
    [string] $Suffix = "preview.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))",
    [string] $Output,
    [switch] $SkipTests,
    [switch] $Push,
    [string] $Owner = 'joost-morsink'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $Output) { $Output = Join-Path $root 'dist' }

function Invoke-Checked {
    param([string] $Command, [string[]] $Arguments, [string] $WorkingDirectory = $root)
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
if ($props -notmatch '<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>') { throw 'no <VersionPrefix> in Directory.Build.props' }
$version = "$($Matches[1])-$Suffix"
Write-Host "Prerelease $version" -ForegroundColor Cyan

$solution = Join-Path $root 'BitWizards.Plugins.sln'
if (-not $SkipTests) {
    Invoke-Checked dotnet @('test', $solution, '--configuration', 'Release')
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
Invoke-Checked dotnet @('pack', $solution, '--configuration', 'Release', '--version-suffix', $Suffix, '--output', $Output)

$packages = Get-ChildItem $Output -Filter "*.$version.nupkg"
if ($Push) {
    if (-not $env:GITHUB_TOKEN) { throw 'set GITHUB_TOKEN to a token that can write packages' }
    foreach ($package in $packages) {
        Invoke-Checked dotnet @('nuget', 'push', $package.FullName, '--source', "https://nuget.pkg.github.com/$Owner/index.json", '--api-key', $env:GITHUB_TOKEN, '--skip-duplicate')
    }
}

Write-Host "`nBuilt $version in $Output" -ForegroundColor Green
$packages | ForEach-Object { Write-Host "  $($_.Name)" }
