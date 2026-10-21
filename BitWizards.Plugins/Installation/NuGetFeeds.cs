using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace BitWizards.Plugins.Installation;

/// <summary>
/// The NuGet feeds that packages are resolved from.
/// Feeds come from the standard NuGet configuration (<c>nuget.config</c> files, merged hierarchically from the
/// working directory upwards, then the user and machine-wide config), or from explicit <c>--source</c> values.
/// Package source mapping from the configuration is honoured.
/// </summary>
public sealed class NuGetFeeds
{
    private readonly record struct Feed(PackageSource Source, SourceRepository Repository, FindPackageByIdResource Resource);

    private readonly IReadOnlyList<Feed> _feeds;
    private readonly PackageSourceMapping? _mapping;
    private readonly TextWriter _log;

    private NuGetFeeds(IReadOnlyList<Feed> feeds, PackageSourceMapping? mapping, TextWriter log)
    {
        _feeds = feeds;
        _mapping = mapping;
        _log = log;
    }

    public IReadOnlyList<PackageSource> Sources => _feeds.Select(f => f.Source).ToList();

    /// <summary>
    /// Loads the feeds.
    /// </summary>
    /// <param name="workingDirectory">Directory to start looking for <c>nuget.config</c> files in.</param>
    /// <param name="sources">Explicit sources (a configured source name, URL or path). Overrides the configured feeds and source mapping.</param>
    /// <param name="configFile">An explicit config file to use instead of the hierarchical lookup.</param>
    /// <param name="log">Where per-source problems are reported (default: standard error).</param>
    public static async Task<NuGetFeeds> LoadAsync(
        string workingDirectory,
        IEnumerable<string>? sources = null,
        string? configFile = null,
        TextWriter? log = null)
    {
        log ??= Console.Error;
        var settings = LoadSettings(workingDirectory, configFile);
        var configured = new PackageSourceProvider(settings).LoadPackageSources().ToList();

        var explicitSources = sources?.ToList() ?? [];
        var packageSources = explicitSources.Count > 0
            ? explicitSources.Select(s => FindConfigured(configured, s) ?? new PackageSource(s)).ToList()
            : configured.Where(s => s.IsEnabled).ToList();
        if (packageSources.Count == 0)
            throw new InvalidOperationException("No enabled NuGet package sources are configured. Add one to a nuget.config or use --source.");

        var feeds = new List<Feed>();
        foreach (var source in packageSources)
        {
            var repository = Repository.Factory.GetCoreV3(source);
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>()
                           ?? throw new InvalidOperationException($"Could not access NuGet source '{source.Name}' ({source.Source}).");
            feeds.Add(new Feed(source, repository, resource));
        }

        var mapping = explicitSources.Count > 0 ? null : PackageSourceMapping.GetPackageSourceMapping(settings);
        return new NuGetFeeds(feeds, mapping is { IsEnabled: true } ? mapping : null, log);
    }

    private static ISettings LoadSettings(string workingDirectory, string? configFile)
    {
        if (configFile is null)
            return Settings.LoadDefaultSettings(workingDirectory);

        var full = Path.GetFullPath(configFile, workingDirectory);
        if (!File.Exists(full))
            throw new FileNotFoundException($"NuGet config file not found: {full}", full);
        return Settings.LoadSpecificSettings(Path.GetDirectoryName(full)!, Path.GetFileName(full));
    }

    private static PackageSource? FindConfigured(IEnumerable<PackageSource> configured, string nameOrSource)
        => configured.FirstOrDefault(s => string.Equals(s.Name, nameOrSource, StringComparison.OrdinalIgnoreCase))
           ?? configured.FirstOrDefault(s => string.Equals(s.Source, nameOrSource, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The feeds that may supply the given package, taking package source mapping into account.
    /// </summary>
    private IEnumerable<Feed> FeedsFor(string packageId)
    {
        if (_mapping is null)
            return _feeds;
        var names = _mapping.GetConfiguredPackageSources(packageId);
        return _feeds.Where(f => names.Contains(f.Source.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// All versions of a package that are available on any applicable feed.
    /// </summary>
    public async Task<IReadOnlyList<NuGetVersion>> GetAllVersionsAsync(
        string packageId,
        SourceCacheContext cache)
    {
        var versions = new HashSet<NuGetVersion>();
        foreach (var feed in FeedsFor(packageId))
        {
            try
            {
                foreach (var version in await feed.Resource.GetAllVersionsAsync(packageId, cache, NullLogger.Instance, CancellationToken.None))
                    versions.Add(version);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Report(feed, $"could not list versions of {packageId}", e);
            }
        }
        return versions.ToList();
    }

    /// <summary>
    /// Copies the .nupkg of a package to the stream, from the first applicable feed that has it.
    /// </summary>
    public async Task<bool> CopyNupkgToStreamAsync(
        PackageIdentity identity,
        MemoryStream destination,
        SourceCacheContext cache)
    {
        foreach (var feed in FeedsFor(identity.Id))
        {
            try
            {
                destination.SetLength(0);
                if (await feed.Resource.CopyNupkgToStreamAsync(identity.Id, identity.Version, destination, cache,
                        NullLogger.Instance, CancellationToken.None))
                    return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Report(feed, $"could not download {identity}", e);
            }
        }
        destination.SetLength(0);
        return false;
    }

    private void Report(Feed feed, string what, Exception e)
        => _log.WriteLine($"NuGet source '{feed.Source.Name}' ({feed.Source.Source}): {what}: {e.GetBaseException().Message}");
}
