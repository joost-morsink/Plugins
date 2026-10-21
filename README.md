# BitWizards.Plugins

Plugins for command line tools, in one place: the plugin system of `giki`, `archvalidator` and `ks`.
How a tool and a plugin use it is in [the README of the package](BitWizards.Plugins/README.md).

| | |
|---|---|
| `BitWizards.Plugins/` | the package: the module contract, the loader, the starting of a tool with plugins, `plugins list`, and `install` from NuGet feeds |
| `test/` | the tests, with two sample plugins that are built and put in a plugin folder; the tests of the installation come from `giki` |

The package has four dependencies: `System.CommandLine`, `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Configuration.Json` for the tool,
and `NuGet.Protocol` (which brings the other NuGet libraries) for installing. `System.Security.Cryptography.Pkcs` is raised above what NuGet asks for, because that has known vulnerabilities.

## Build

```bash
dotnet test BitWizards.Plugins.sln
dotnet pack BitWizards.Plugins.sln --configuration Release --output dist
dotnet pack BitWizards.Plugins.sln --configuration Release --output dist --version-suffix preview.1   # 0.1.0-preview.1
```

`scripts/Build-Prerelease.ps1` does the last one with a suffix from the date and time (`0.1.0-preview.20261021180000`), after the tests;
`-Push` also pushes it to GitHub Packages (with a token in `GITHUB_TOKEN`), `-Suffix rc.1` and `-SkipTests` are there as well.

## Releasing

The version is the `VersionPrefix` in `Directory.Build.props`, for all assemblies and packages.

| Workflow | When | What |
|---|---|---|
| `ci.yml` | a push to `main`, a pull request | tests on Linux, Windows and macOS, and a pack |
| `publish.yml` | a tag `v<version>`, such as `v0.1.0` | tests, packs and pushes the packages to GitHub Packages of the owner of the repository, and keeps them in a GitHub release |
| `publish.yml` | a tag with a suffix, such as `v0.2.0-rc.1` | the same, as a prerelease (the part before the suffix must be the `VersionPrefix`) |
| `publish.yml` | run by hand on `main` | a prerelease `<version>-preview.<run number>` |

A tag that is not the version of `Directory.Build.props` fails the workflow before anything is published.
To use the packages, add `https://nuget.pkg.github.com/<owner>/index.json` as a NuGet source with a token that can read packages.
