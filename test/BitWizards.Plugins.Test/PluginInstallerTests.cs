using AwesomeAssertions;
using BitWizards.Plugins.Installation;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace BitWizards.Plugins.Test;

[TestClass]
public class PluginInstallerTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "plugins-nuget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private string Feed(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Pack(string feed, string id, string version, params (string Id, string Range)[] dependencies)
        => PackWith(feed, id, version, dependencies, []);

    private static void PackWith(string feed, string id, string version,
        (string Id, string Range)[] dependencies, string[] extraFiles, string? libDll = null, string? libSource = null,
        bool lib = true)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = id
        };
        builder.Authors.Add("test");
        var libTarget = $"lib/net10.0/{libDll ?? id}.dll";
        if (!lib)
        {
        }
        else if (libSource is null)
            builder.Files.Add(new InMemoryFile(libTarget, id + version));
        else
            builder.Files.Add(new PhysicalPackageFile { SourcePath = libSource, TargetPath = libTarget });
        foreach (var extra in extraFiles)
            builder.Files.Add(new InMemoryFile(extra, extra));
        if (dependencies.Length > 0)
            builder.DependencyGroups.Add(new PackageDependencyGroup(
                NuGetFramework.Parse("net10.0"),
                dependencies.Select(d => new PackageDependency(d.Id, VersionRange.Parse(d.Range)))));
        using var stream = File.Create(Path.Combine(feed, $"{id}.{version}.nupkg"));
        builder.Save(stream);
    }

    private static void WriteConfig(string directory, string fileName, string sources, string extra = "")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                {sources}
              </packageSources>
              {extra}
            </configuration>
            """);
    }

    private async Task<(bool Success, string PluginDir)> Install(NuGetFeeds feeds, string id, string version = "latest",
        HostAssemblies? host = null, TextWriter? log = null, bool allowPrerelease = false)
    {
        var plugins = Path.Combine(_root, "plugins-" + Guid.NewGuid().ToString("N"));
        var success = await PluginInstaller.InstallAsync(id, version, plugins, feeds, host,
            log ?? TextWriter.Null, allowPrerelease: allowPrerelease);
        return (success, plugins);
    }

    private async Task<NuGetFeeds> SingleFeed(string feed)
    {
        WriteConfig(_root, "nuget.config", $"""<add key="local" value="{feed}" />""");
        return await NuGetFeeds.LoadAsync(_root);
    }

    private static HostAssemblies Host(string[] assemblies, params (string Id, string Version)[] packages)
        => HostAssemblies.Create(
            assemblies.Select(a => new KeyValuePair<string, Version?>(a, null)),
            packages.Select(p => new KeyValuePair<string, NuGetVersion>(p.Id, NuGetVersion.Parse(p.Version))));

    private static string[] Files(string directory)
        => Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(x => x).ToArray()!;

    [TestMethod]
    public async Task HostProvidedPackageIsSkippedWithItsDependencies()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Tool.Core", "[1.0.0,)"), ("Own", "[1.0.0,)"));
        Pack(feed, "Tool.Core", "1.0.0", ("Sub", "[1.0.0,)"));
        Pack(feed, "Sub", "1.0.0");
        Pack(feed, "Own", "1.0.0");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug",
            host: Host([], ("Tool.Core", "1.0.0")));

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Own.dll", "Plug.dll");
    }

    [TestMethod]
    public async Task HostProvidedAssemblyPrunesPackageWithoutDeps()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Tool.Core", "[1.0.0,)"));
        Pack(feed, "Tool.Core", "1.0.0", ("Sub", "[1.0.0,)"));
        Pack(feed, "Sub", "1.0.0");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug",
            host: Host(["Tool.Core"]));

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Plug.dll");
    }

    [TestMethod]
    public async Task FacadePackageIsNotCopiedOrWalked()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Facade.Pkg", "[1.0.0,)"));
        PackWith(feed, "Facade.Pkg", "1.0.0", [("Sub", "[1.0.0,)")], [], libDll: "System.Fake");
        Pack(feed, "Sub", "1.0.0");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug",
            host: Host(["System.Fake"]));

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Plug.dll");
    }

    [TestMethod]
    public async Task NewerPackageVersionThanHostWarnsAndUsesTheHostsAssembly()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Dep.Pkg", "[2.0.0,)"));
        PackWith(feed, "Dep.Pkg", "2.0.0", [("Sub", "[1.0.0,)")], [], libDll: "Dep");
        Pack(feed, "Sub", "1.0.0");
        var log = new StringWriter();

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug",
            host: Host(["Dep"], ("Dep.Pkg", "1.0.0")), log: log);

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Plug.dll");
        log.ToString().Should().Contain("Warning: Dep.Pkg").And.Contain("host provides 1.0.0");
    }

    [TestMethod]
    public async Task LegacyFacadeWithOnlyReferenceAssembliesIsNotWalked()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Legacy.Facade", "[4.3.0,)"));
        PackWith(feed, "Legacy.Facade", "4.3.0", [("Sub", "[1.0.0,)")], ["ref/netstandard1.3/System.Fake.dll"], lib: false);
        Pack(feed, "Sub", "1.0.0");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug", host: Host(["System.Fake"]));

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Plug.dll");
    }

    [TestMethod]
    public async Task PackageWithOnlyReferenceAssembliesNotProvidedByHostIsStillWalked()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Legacy.Facade", "[4.3.0,)"));
        PackWith(feed, "Legacy.Facade", "4.3.0", [("Sub", "[1.0.0,)")], ["ref/netstandard1.3/System.Fake.dll"], lib: false);
        Pack(feed, "Sub", "1.0.0");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug", host: Host(["Other"]));

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Plug.dll", "Sub.dll");
    }

    [TestMethod]
    public async Task PluginAssemblyNewerThanHostAssemblyFails()
    {
        var feed = Feed("feed");
        var realDll = typeof(PluginInstaller).Assembly.Location;
        Pack(feed, "Plug", "1.0.0", ("Tool.Main", "[1.0.0,)"));
        PackWith(feed, "Tool.Main", "1.0.0", [], [], libDll: "Tool.Main", libSource: realDll);
        var feeds = await SingleFeed(feed);

        var act = () => Install(feeds, "Plug", host: HostAssemblies.Create(
            [new KeyValuePair<string, Version?>("Tool.Main", new Version(0, 0, 1, 0))]));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*host provides 0.0.1.0*");
    }

    [TestMethod]
    public async Task AllAssemblyConflictsAreReportedTogether()
    {
        var feed = Feed("feed");
        var realDll = typeof(PluginInstaller).Assembly.Location;
        Pack(feed, "Plug", "1.0.0", ("One", "[1.0.0,)"), ("Two", "[1.0.0,)"));
        PackWith(feed, "One", "1.0.0", [("Deeper", "[1.0.0,)")], [], libDll: "Host.One", libSource: realDll);
        PackWith(feed, "Two", "1.0.0", [], [], libDll: "Host.Two", libSource: realDll);
        PackWith(feed, "Deeper", "1.0.0", [], [], libDll: "Host.Three", libSource: realDll);
        var feeds = await SingleFeed(feed);
        var old = new Version(0, 0, 1, 0);

        var act = () => Install(feeds, "Plug", host: HostAssemblies.Create(
            new[] { "Host.One", "Host.Two", "Host.Three" }.Select(n => new KeyValuePair<string, Version?>(n, old))));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("One.1.0.0").And.Contain("Two.1.0.0").And.Contain("Deeper.1.0.0");
    }

    [TestMethod]
    public async Task PluginAssemblyNotNewerThanHostAssemblyIsSkipped()
    {
        var feed = Feed("feed");
        var realDll = typeof(PluginInstaller).Assembly.Location;
        PackWith(feed, "Plug", "1.0.0", [], [], libDll: "Tool.Main", libSource: realDll);

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug", host: HostAssemblies.Create(
            [new KeyValuePair<string, Version?>("Tool.Main", new Version(99, 0, 0, 0))]));

        success.Should().BeTrue();
        Files(plugins).Should().BeEmpty();
    }

    [TestMethod]
    public async Task WithoutHostEverythingIsInstalled()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Tool.Core", "[1.0.0,)"));
        Pack(feed, "Tool.Core", "1.0.0", ("Sub", "[1.0.0,)"));
        Pack(feed, "Sub", "1.0.0");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug", host: null);

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Plug.dll", "Sub.dll", "Tool.Core.dll");
    }

    [TestMethod]
    public async Task UnprovidedDependencyChainIsWalkedInFull()
    {
        var feed = Feed("feed");
        Pack(feed, "A", "1.0.0", ("B", "[1.0.0,)"));
        Pack(feed, "B", "1.0.0", ("C", "[1.0.0,)"));
        Pack(feed, "C", "1.0.0");

        var (success, plugins) = await Install(await SingleFeed(feed), "A", host: Host(["Other"]));

        success.Should().BeTrue();
        Files(plugins).Should().Equal("A.dll", "B.dll", "C.dll");
    }

    [TestMethod]
    public async Task RuntimesAreNotCopiedAndNativeAssetsAreReported()
    {
        var feed = Feed("feed");
        PackWith(feed, "Plug", "1.0.0", [],
            ["runtimes/linux-x64/native/libplug.so", "runtimes/win/lib/net10.0/Plug.Win.dll"]);
        var log = new StringWriter();

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug", log: log);

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Plug.dll");
        log.ToString().Should().Contain("native assets");
    }

    [TestMethod]
    public void DiscoverFindsFrameworkAndAppAssemblies()
    {
        var host = HostAssemblies.Discover();

        host.TryGetAssembly("System.Private.CoreLib", out var version).Should().BeTrue();
        version.Should().NotBeNull();
        host.TryGetAssembly("BitWizards.Plugins", out _).Should().BeTrue();
        host.TryGetAssembly("Not.Provided.Anywhere", out _).Should().BeFalse();
    }

    [TestMethod]
    public void CleanEmptiesTheDirectory()
    {
        var dir = Feed("plugins");
        File.WriteAllText(Path.Combine(dir, "old.dll"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "old2.dll"), "x");

        var removed = PluginCleaner.Clean(dir, [], TextWriter.Null);

        removed.Should().Be(2);
        Directory.Exists(dir).Should().BeTrue();
        Directory.GetFileSystemEntries(dir).Should().BeEmpty();
    }

    [TestMethod]
    public void CleanRefusesDirectoriesContainingProtectedPaths()
    {
        var dir = Feed("plugins");
        var inside = Path.Combine(dir, "tool");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "tool.dll"), "x");

        var act = () => PluginCleaner.Clean(dir, [inside], TextWriter.Null);
        var same = () => PluginCleaner.Clean(dir, [dir], TextWriter.Null);

        act.Should().Throw<InvalidOperationException>();
        same.Should().Throw<InvalidOperationException>();
        File.Exists(Path.Combine(inside, "tool.dll")).Should().BeTrue();
    }

    [TestMethod]
    public void CleanRefusesRootDirectory()
    {
        var act = () => PluginCleaner.Clean(Path.GetPathRoot(_root)!, [], TextWriter.Null);

        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void CleanAllowsProtectedPathsOutsideTheDirectory()
    {
        var dir = Feed("plugins");
        var outside = Feed("elsewhere");

        var act = () => PluginCleaner.Clean(dir, [outside], TextWriter.Null);

        act.Should().NotThrow();
    }

    [TestMethod]
    public async Task ReadsLowercaseNuGetConfigFromParentDirectory()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0");
        WriteConfig(_root, "nuget.config", $"""<add key="local" value="{feed}" />""");
        var working = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(working);

        var feeds = await NuGetFeeds.LoadAsync(working);

        feeds.Sources.Select(s => s.Name).Should().Equal("local");
        var (success, plugins) = await Install(feeds, "Plug");
        success.Should().BeTrue();
        File.Exists(Path.Combine(plugins, "Plug.dll")).Should().BeTrue();
    }

    [TestMethod]
    public async Task ClearAndDisabledSourcesAreHonoured()
    {
        var feed = Feed("feed");
        var other = Feed("other");
        WriteConfig(_root, "NuGet.Config",
            $"""
             <add key="local" value="{feed}" />
             <add key="other" value="{other}" />
             """,
            """<disabledPackageSources><add key="other" value="true" /></disabledPackageSources>""");

        var feeds = await NuGetFeeds.LoadAsync(_root);

        feeds.Sources.Select(s => s.Name).Should().Equal("local");
    }

    [TestMethod]
    public async Task SourceOptionOverridesConfiguration()
    {
        var configured = Feed("configured");
        var explicitFeed = Feed("explicit");
        Pack(explicitFeed, "Plug", "2.0.0");
        WriteConfig(_root, "nuget.config", $"""<add key="local" value="{configured}" />""");

        var feeds = await NuGetFeeds.LoadAsync(_root, [explicitFeed]);

        var (success, _) = await Install(feeds, "Plug");
        success.Should().BeTrue();
    }

    [TestMethod]
    public async Task SourceOptionCanNameAConfiguredSource()
    {
        var a = Feed("a");
        var b = Feed("b");
        WriteConfig(_root, "nuget.config", $"""
            <add key="a" value="{a}" />
            <add key="b" value="{b}" />
            """);

        var feeds = await NuGetFeeds.LoadAsync(_root, ["b"]);

        feeds.Sources.Select(s => s.Name).Should().Equal("b");
    }

    [TestMethod]
    public async Task ConfigFileOptionIsUsedInsteadOfLookup()
    {
        var feed = Feed("feed");
        var explicitFeed = Feed("explicit");
        WriteConfig(_root, "nuget.config", $"""<add key="local" value="{feed}" />""");
        WriteConfig(Path.Combine(_root, "elsewhere"), "my.config", $"""<add key="chosen" value="{explicitFeed}" />""");

        var feeds = await NuGetFeeds.LoadAsync(_root, configFile: Path.Combine(_root, "elsewhere", "my.config"));

        feeds.Sources.Select(s => s.Name).Should().Equal("chosen");
    }

    [TestMethod]
    public async Task LatestIsResolvedAcrossFeeds()
    {
        var a = Feed("a");
        var b = Feed("b");
        Pack(a, "Plug", "1.0.0");
        Pack(b, "Plug", "1.5.0");
        WriteConfig(_root, "nuget.config", $"""
            <add key="a" value="{a}" />
            <add key="b" value="{b}" />
            """);

        var feeds = await NuGetFeeds.LoadAsync(_root);

        var (success, plugins) = await Install(feeds, "Plug");
        success.Should().BeTrue();
        File.ReadAllText(Path.Combine(plugins, "Plug.dll")).Should().Be("Plug1.5.0");
    }

    [TestMethod]
    public async Task DependenciesAreResolvedFromOtherFeeds()
    {
        var a = Feed("a");
        var b = Feed("b");
        Pack(a, "Plug", "1.0.0", ("Dep", "[1.0.0,)"));
        Pack(b, "Dep", "1.2.0");
        WriteConfig(_root, "nuget.config", $"""
            <add key="a" value="{a}" />
            <add key="b" value="{b}" />
            """);

        var feeds = await NuGetFeeds.LoadAsync(_root);

        var (success, plugins) = await Install(feeds, "Plug");
        success.Should().BeTrue();
        File.Exists(Path.Combine(plugins, "Dep.dll")).Should().BeTrue();
    }

    [TestMethod]
    public async Task DependencyResolvesToLowestSatisfyingVersionNotNewestPrerelease()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Dep", "13.0.4"));
        Pack(feed, "Dep", "13.0.3");
        Pack(feed, "Dep", "13.0.4");
        Pack(feed, "Dep", "13.0.9");
        Pack(feed, "Dep", "14.0.1-beta2");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug");

        success.Should().BeTrue();
        File.ReadAllText(Path.Combine(plugins, "Dep.dll")).Should().Be("Dep13.0.4");
    }

    [TestMethod]
    public async Task LatestPrefersStableOverPrerelease()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0");
        Pack(feed, "Plug", "1.1.0-beta1");

        var (_, plugins) = await Install(await SingleFeed(feed), "Plug");

        File.ReadAllText(Path.Combine(plugins, "Plug.dll")).Should().Be("Plug1.0.0");
    }

    [TestMethod]
    public async Task PackageSourceMappingRestrictsFeeds()
    {
        var a = Feed("a");
        var b = Feed("b");
        Pack(a, "Plug", "1.0.0");
        Pack(b, "Plug", "9.0.0");
        WriteConfig(_root, "nuget.config", $"""
            <add key="a" value="{a}" />
            <add key="b" value="{b}" />
            """,
            """
            <packageSourceMapping>
              <packageSource key="a"><package pattern="Plug" /></packageSource>
              <packageSource key="b"><package pattern="Other.*" /></packageSource>
            </packageSourceMapping>
            """);

        var feeds = await NuGetFeeds.LoadAsync(_root);

        var (success, plugins) = await Install(feeds, "Plug");
        success.Should().BeTrue();
        File.ReadAllText(Path.Combine(plugins, "Plug.dll")).Should().Be("Plug1.0.0");
    }

    [TestMethod]
    public async Task UnknownPackageFails()
    {
        var feed = Feed("feed");
        WriteConfig(_root, "nuget.config", $"""<add key="local" value="{feed}" />""");
        var feeds = await NuGetFeeds.LoadAsync(_root);

        var act = () => Install(feeds, "Missing");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task NoEnabledSourcesIsAnError()
    {
        var feed = Feed("feed");
        WriteConfig(_root, "nuget.config", $"""<add key="local" value="{feed}" />""",
            """<disabledPackageSources><add key="local" value="true" /></disabledPackageSources>""");

        var act = () => NuGetFeeds.LoadAsync(_root);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No enabled NuGet package sources*");
    }

    [TestMethod]
    public async Task LatestIgnoresPrereleaseByDefault()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0");
        Pack(feed, "Plug", "2.0.0-beta.1");

        var (_, plugins) = await Install(await SingleFeed(feed), "Plug");

        File.ReadAllText(Path.Combine(plugins, "Plug.dll")).Should().Be("Plug1.0.0");
    }

    [TestMethod]
    public async Task LatestUsesPrereleaseWhenAllowed()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0");
        Pack(feed, "Plug", "2.0.0-beta.1");

        var (_, plugins) = await Install(await SingleFeed(feed), "Plug", allowPrerelease: true);

        File.ReadAllText(Path.Combine(plugins, "Plug.dll")).Should().Be("Plug2.0.0-beta.1");
    }

    [TestMethod]
    public async Task LatestWithOnlyPrereleasesFailsWithHint()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0-beta.1");

        var act = async () => await Install(await SingleFeed(feed), "Plug");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*--prerelease*");
    }

    [TestMethod]
    public async Task ExplicitPrereleaseVersionIsInstalledWithPrereleaseDependencies()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0-beta.1", ("Dep", "[1.0.0-beta.1,)"));
        Pack(feed, "Dep", "1.0.0-beta.1");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug", "1.0.0-beta.1");

        success.Should().BeTrue();
        Files(plugins).Should().Equal("Dep.dll", "Plug.dll");
    }

    [TestMethod]
    public async Task PrereleaseDependencyIsRejectedWhenNotAllowed()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Dep", "[1.0.0,)"));
        Pack(feed, "Dep", "1.0.1-beta.1");
        var feeds = await SingleFeed(feed);

        var act = async () => await Install(feeds, "Plug");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*--prerelease*");

        var (success, _) = await Install(feeds, "Plug", allowPrerelease: true);
        success.Should().BeTrue();
    }

    [TestMethod]
    public async Task PrereleaseLowerBoundInDependencyRangeAllowsPrerelease()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Dep", "[1.0.0-beta.1,)"));
        Pack(feed, "Dep", "1.0.0-beta.1");
        Pack(feed, "Dep", "1.0.0");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug");

        success.Should().BeTrue();
        File.ReadAllText(Path.Combine(plugins, "Dep.dll")).Should().Be("Dep1.0.0-beta.1");
    }

    [TestMethod]
    public async Task PrereleaseUpperBoundInDependencyRangeAllowsPrerelease()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Dep", "(,2.0.0-rc.1]"));
        Pack(feed, "Dep", "2.0.0-rc.1");

        var (success, plugins) = await Install(await SingleFeed(feed), "Plug");

        success.Should().BeTrue();
        File.ReadAllText(Path.Combine(plugins, "Dep.dll")).Should().Be("Dep2.0.0-rc.1");
    }

    [TestMethod]
    public async Task PrereleaseOptInIsNotTransitive()
    {
        var feed = Feed("feed");
        Pack(feed, "Plug", "1.0.0", ("Dep", "[1.0.0-beta.1,)"));
        Pack(feed, "Dep", "1.0.0-beta.1", ("Leaf", "[1.0.0,)"));
        Pack(feed, "Leaf", "2.0.0-beta.1");

        var act = async () => await Install(await SingleFeed(feed), "Plug");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Leaf*--prerelease*");
    }

    [TestMethod]
    [DataRow("[1.2.0, )", "1.2.0", true, "the minimum is there: nothing pushed since can be lower")]
    [DataRow("1.2.0", "1.2.0", true, "a bare version is a minimum")]
    [DataRow("[1.2.0, )", "1.3.0", false, "the minimum is not in the list: it may have been pushed since")]
    [DataRow("(1.2.0, )", "1.3.0", false, "an exclusive minimum is never in the list")]
    [DataRow("(, 2.0.0)", "1.0.0", false, "without a minimum something lower may have been pushed")]
    [DataRow("[1.2.0, )", null, false, "nothing was found")]
    [DataRow("[1.2.0-beta.1, )", "1.2.0-beta.1", true, "a prerelease minimum counts as well")]
    public void AListOfVersionsThatHasTheMinimumOfTheRangeIsNotFetchedAgain(string range, string? lowest, bool final, string because)
        => PluginInstaller.IsFinal(VersionRange.Parse(range), lowest is null ? null : NuGetVersion.Parse(lowest)).Should().Be(final, because);

    [TestMethod]
    [DataRow("0.4.0", false)]
    [DataRow("0.5.0-beta.1", true)]
    [DataRow("0.5.0-beta.1+abc123", true)]
    [DataRow("0.5.0+abc123", false)]
    [DataRow(null, false)]
    public void ToolVersionDetectsPrerelease(string? version, bool expected)
        => ToolVersion.IsPrerelease(version).Should().Be(expected);

    private sealed class InMemoryFile(string path, string content) : IPackageFile
    {
        public string Path => path;
        public string? EffectivePath => path;
        public System.Runtime.Versioning.FrameworkName? TargetFramework => null;
        public NuGetFramework NuGetFramework => NuGetFramework.Parse("net10.0");
        public DateTimeOffset LastWriteTime => DateTimeOffset.UtcNow;
        public Stream GetStream() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
    }
}
