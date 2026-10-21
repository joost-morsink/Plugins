using System.Reflection;
using System.Runtime.Versioning;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace BitWizards.Plugins.Installation;

public static class PluginInstaller
{
    /// <summary>
    /// Installs a package and its dependencies: walks the dependency graph, downloads each package from the first feed that has it,
    /// and copies the managed assemblies for the target framework into the plugin directory.
    /// </summary>
    /// <param name="host">
    /// What the running tool already provides. Packages and assemblies provided by the host are not installed,
    /// and the dependencies of a package that is entirely provided by the host are not walked.
    /// Null disables this pruning.
    /// </param>
    /// <param name="framework">
    /// The framework that the plugins run on: the one the tool targets (<see cref="FrameworkOf"/>; the install command passes that of the tool),
    /// the framework of this library by default. The assemblies of the nearest framework in each package are installed.
    /// </param>
    /// <param name="allowPrerelease">
    /// Whether prerelease versions are considered when resolving "latest" and dependencies. An explicitly given
    /// prerelease version is always installed, and allows prerelease dependencies for that install.
    /// </param>
    /// <param name="refresh">
    /// Whether the lists of versions are fetched fresh. NuGet's http cache keeps them for 30 minutes, which is what is used by default:
    /// a version that was pushed in that time is not found. Packages themselves always come through the cache, as a version never changes.
    /// </param>
    public static async Task<bool> InstallAsync(
        string packageId,
        string version,
        string pluginDirectory,
        NuGetFeeds feeds,
        HostAssemblies? host = null,
        TextWriter? log = null,
        NuGetFramework? framework = null,
        bool allowPrerelease = false,
        bool refresh = false)
    {
        Directory.CreateDirectory(pluginDirectory);

        var explicitVersion = version == "latest" ? null : NuGetVersion.Parse(version);
        var walk = new Walk(pluginDirectory, feeds, host, log ?? Console.Error, framework ?? FrameworkOf(null),
            allowPrerelease || explicitVersion is { IsPrerelease: true }, refresh);

        var packageVersion = explicitVersion ?? NuGetVersion.Parse(await walk.FindOutLatest(packageId));
        var success = await walk.Package(new PackageIdentity(packageId, packageVersion), new VersionRange(packageVersion));
        walk.ReportSummary();
        if (walk.Conflicts.Count > 0)
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, walk.Conflicts.Prepend("Assembly version conflicts with the host:")) +
                Environment.NewLine +
                "Use a plugin version built for this version of the tool, or --include-provided to install it anyway.");
        return success;
    }

    /// <summary>The framework that an assembly targets (<c>net10.0</c>), which is the one the plugins of that tool run on. Falls back to the framework this library runs on.</summary>
    public static NuGetFramework FrameworkOf(Assembly? tool)
    {
        var name = tool?.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
                   ?? typeof(PluginInstaller).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
                   ?? ".NETCoreApp,Version=v10.0";
        return NuGetFramework.ParseFrameworkName(name, DefaultFrameworkNameProvider.Instance);
    }

    private sealed class Walk(
        string pluginDirectory,
        NuGetFeeds feeds,
        HostAssemblies? host,
        TextWriter log,
        NuGetFramework framework,
        bool allowPrerelease,
        bool refresh)
    {
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
        // NuGet's http cache keeps a list of versions for 30 minutes, so a package pushed after an earlier install is missing from it,
        // and a dependency on the version just released does not resolve: that is what refresh is for.
        private readonly SourceCacheContext _lists = new() { NoCache = refresh };
        // The package of a version never changes, so downloads can always come from the cache.
        private readonly SourceCacheContext _cache = new();
        private int _packages, _assemblies, _skippedPackages, _skippedAssemblies;

        /// <summary>
        /// Plugin assemblies that are newer than the host's copy of the same assembly.
        /// </summary>
        public List<string> Conflicts { get; } = [];

        public async Task<string> FindOutLatest(string packageId)
        {
            var versions = await feeds.GetAllVersionsAsync(packageId, _lists);
            var candidates = allowPrerelease ? versions : versions.Where(v => !v.IsPrerelease).ToList();
            return candidates.OrderByDescending(v => v).FirstOrDefault()?.ToString()
                   ?? throw new InvalidOperationException(versions.Count > 0
                       ? $"Package {packageId} only has prerelease versions; use --prerelease or --version to install one."
                       : $"Could not find any versions for package {packageId}.");
        }

        public void ReportSummary()
        {
            log.WriteLine($"Installed {_assemblies} assemblies from {_packages} packages.");
            if (host is not null && (_skippedPackages > 0 || _skippedAssemblies > 0))
                log.WriteLine(
                    $"Skipped {_skippedPackages} packages and {_skippedAssemblies} assemblies provided by the host ({host.Description}).");
        }

        public async Task<bool> Package(PackageIdentity identity, VersionRange required)
        {
            if (!_visited.Add($"{identity.Id}/{identity.Version.ToNormalizedString()}"))
                return true;

            if (ProvidedByHostPackage(identity.Id, required))
                return true;

            await using var packageStream = new MemoryStream();
            if (!await feeds.CopyNupkgToStreamAsync(identity, packageStream, _cache))
                return false;

            packageStream.Seek(0, SeekOrigin.Begin);
            using var packageReader = new PackageArchiveReader(packageStream);

            var libItems = await packageReader.GetLibItemsAsync(CancellationToken.None);
            var nearestLib = NuGetFrameworkUtility.GetNearest(libItems, framework, x => x.TargetFramework);
            var dlls = nearestLib?.Items.Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];

            // Legacy facade packages (System.Net.Http 4.3.x and friends) have no lib assembly for this framework;
            // their implementation is part of the runtime. Their reference assemblies show what they stand for.
            if (host is not null && dlls.Count == 0 && await ProvidedByHostReferences(identity, packageReader))
                return true;

            var copied = 0;
            var conflicted = false;
            foreach (var file in dlls)
            {
                if (ProvidedByHostAssembly(identity, packageReader, file, out var conflict))
                {
                    conflicted |= conflict;
                    continue;
                }
                await CopyPackageFileAsync(packageReader, file);
                copied++;
            }
            _assemblies += copied;

            // A package whose assemblies the host provides is not walked further: its dependencies are the host's too.
            // A conflicting package is walked on, so that all conflicts are reported at once.
            if (host is not null && dlls.Count > 0 && copied == 0 && !conflicted)
            {
                _skippedPackages++;
                log.WriteLine(host.TryGetPackage(identity.Id, out var hostVersion) && !required.Satisfies(hostVersion)
                    ? $"Warning: {identity.Id} {required.PrettyPrint()} is requested, but the host provides {hostVersion}; using the host's"
                    : $"Skipped {identity} (provided by the host)");
                return true;
            }

            _packages++;
            await WarnAboutNativeAssets(identity, packageReader);

            var dependencyGroups = await packageReader.GetPackageDependenciesAsync(CancellationToken.None);
            var nearestDependencyFramework = new FrameworkReducer().GetNearest(
                framework,
                dependencyGroups.Select(group => group.TargetFramework));
            var nearestDependencyGroup =
                dependencyGroups.FirstOrDefault(group => group.TargetFramework == nearestDependencyFramework);

            if (nearestDependencyGroup is null)
                return true;

            foreach (var dependency in nearestDependencyGroup.Packages)
            {
                if (ProvidedByHostPackage(dependency.Id, dependency.VersionRange))
                    continue;

                var dependencyVersion = await ResolveDependencyVersionAsync(dependency);
                if (!await Package(new PackageIdentity(dependency.Id, dependencyVersion), dependency.VersionRange))
                    return false;
            }

            return true;
        }

        private bool ProvidedByHostPackage(string id, VersionRange required)
        {
            if (host is null || !host.TryGetPackage(id, out var hostVersion))
                return false;

            // A different package version is not necessarily a problem (patch releases usually keep the assembly
            // version), so let the assembly check on the downloaded package decide.
            if (!required.Satisfies(hostVersion))
                return false;

            _skippedPackages++;
            log.WriteLine($"Skipped {id} {hostVersion} (provided by the host)");
            return true;
        }

        private async Task<bool> ProvidedByHostReferences(PackageIdentity identity, PackageArchiveReader packageReader)
        {
            var refGroups = await packageReader.GetItemsAsync(PackagingConstants.Folders.Ref, CancellationToken.None);
            var nearestRef = NuGetFrameworkUtility.GetNearest(refGroups, framework, x => x.TargetFramework);
            var names = nearestRef?.Items
                .Where(x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .ToList() ?? [];
            if (names.Count == 0 || !names.All(name => host!.TryGetAssembly(name!, out _)))
                return false;

            _skippedPackages++;
            _skippedAssemblies += names.Count;
            log.WriteLine($"Skipped {identity} (provided by the host)");
            return true;
        }

        private bool ProvidedByHostAssembly(PackageIdentity identity, PackageArchiveReader packageReader, string file,
            out bool conflict)
        {
            conflict = false;
            var name = Path.GetFileNameWithoutExtension(file);
            if (host is null || !host.TryGetAssembly(name, out var hostVersion))
                return false;

            using var stream = packageReader.GetStream(file);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Seek(0, SeekOrigin.Begin);
            var packageVersion = HostAssemblies.ReadAssemblyVersion(copy);
            conflict = packageVersion is not null && hostVersion is not null && packageVersion > hostVersion;
            if (conflict)
                Conflicts.Add($"{identity} contains {name} {packageVersion}, but the host provides {hostVersion}.");

            _skippedAssemblies++;
            return true;
        }

        private async Task WarnAboutNativeAssets(PackageIdentity identity, PackageArchiveReader packageReader)
        {
            var runtimeItems = await packageReader.GetItemsAsync(PackagingConstants.Folders.Runtimes, CancellationToken.None);
            if (runtimeItems.SelectMany(group => group.Items).Any(x => x.Contains("/native/", StringComparison.OrdinalIgnoreCase)))
                log.WriteLine(
                    $"Warning: {identity} contains native assets (runtimes/*/native), which are not installed. " +
                    "Plugins are expected to consist of managed assemblies only.");
        }

        private async Task<NuGetVersion> ResolveDependencyVersionAsync(PackageDependency dependency)
        {
            // Like NuGet restore: the lowest version that satisfies the range, so an open-ended range
            // such as "13.0.4" does not pull in the newest (possibly prerelease) version on the feed.
            var versions = await feeds.GetAllVersionsAsync(dependency.Id, _lists);
            // A range that names a prerelease bound is the depending package stating that it is stable with
            // that prerelease, so that dependency may resolve to prereleases without --prerelease.
            var range = dependency.VersionRange;
            var prereleaseAllowed = allowPrerelease || range.MinVersion is { IsPrerelease: true } ||
                                    range.MaxVersion is { IsPrerelease: true };
            var candidates = prereleaseAllowed ? versions : versions.Where(v => !v.IsPrerelease).ToList();
            // Not VersionRange.FindBestMatch: that ignores prereleases unless a range bound is itself a prerelease.
            var matchingVersion = LowestSatisfying(range, candidates);
            if (matchingVersion is not null)
                return matchingVersion;

            var hint = !prereleaseAllowed && LowestSatisfying(range, versions) is not null
                ? " Only prerelease versions match; use --prerelease to allow them."
                : "";
            throw new InvalidOperationException(
                $"Could not find a version of package {dependency.Id} matching range {dependency.VersionRange}.{hint}");
        }

        private static NuGetVersion? LowestSatisfying(VersionRange range, IEnumerable<NuGetVersion> versions)
            => versions.Where(range.Satisfies).OrderBy(v => v).FirstOrDefault();

        private async Task CopyPackageFileAsync(PackageArchiveReader packageReader, string packageFilePath)
        {
            var outputPath = Path.Combine(pluginDirectory, Path.GetFileName(packageFilePath));

            await using var input = packageReader.GetStream(packageFilePath);
            using var content = new MemoryStream();
            await input.CopyToAsync(content);

            // A plugin directory holds one copy of each assembly: when two packages ship the same file,
            // the highest assembly version wins, whatever order the dependencies are walked in.
            if (File.Exists(outputPath))
            {
                var existingVersion = HostAssemblies.ReadAssemblyVersion(outputPath);
                content.Seek(0, SeekOrigin.Begin);
                var packageVersion = HostAssemblies.ReadAssemblyVersion(content);
                if (existingVersion is not null && packageVersion is not null && existingVersion >= packageVersion)
                {
                    if (existingVersion > packageVersion)
                        log.WriteLine(
                            $"Warning: {Path.GetFileName(outputPath)} {packageVersion} is requested, but {existingVersion} is already installed; keeping the higher version");
                    return;
                }
            }

            content.Seek(0, SeekOrigin.Begin);
            await using var output = File.Create(outputPath);
            await content.CopyToAsync(output);
        }
    }
}
