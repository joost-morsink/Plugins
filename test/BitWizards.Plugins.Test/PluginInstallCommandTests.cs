using System.CommandLine;
using System.CommandLine.Invocation;
using AwesomeAssertions;
using BitWizards.Plugins.Installation;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;

namespace BitWizards.Plugins.Test;

/// <summary>The <c>install</c> command of a tool: a package from a feed ends up in the plugin folder, where the next run of the tool loads it.</summary>
[TestClass]
public class PluginInstallCommandTests
{
    private const string Hello = "BitWizards.Plugins.Test.HelloPlugin";

    private static string Folder(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "bitwizards-plugin-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A feed (a folder) with a package that holds the hello plugin.</summary>
    private static string FeedWithHelloPlugin(string id = "Hello.Plugin")
    {
        var feed = Folder("feed");
        var builder = new PackageBuilder { Id = id, Version = NuGetVersion.Parse("1.0.0"), Description = id };
        builder.Authors.Add("test");
        builder.Files.Add(new PhysicalPackageFile
        {
            SourcePath = Path.Combine(AppContext.BaseDirectory, "samples", Hello, Hello + ".dll"),
            TargetPath = $"lib/net10.0/{Hello}.dll"
        });
        using var stream = File.Create(Path.Combine(feed, $"{id}.1.0.0.nupkg"));
        builder.Save(stream);
        return feed;
    }

    /// <summary>Starts a tool with the plugin folder and runs a command line on it.</summary>
    private static async Task<(int Code, string Output, string Error)> Tool(string plugins, PluginHostOptions options, params string[] args)
    {
        string[] line = ["--plugins", plugins, .. args];
        using var host = PluginHost.Build(line, options);
        var root = new RootCommand("tool");
        host.ConfigureRoot(root);
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = await host.InvokeAsync(root.Parse(line), new InvocationConfiguration { Output = output, Error = error });
        return (code, output.ToString().Replace("\r\n", "\n"), error.ToString().Replace("\r\n", "\n"));
    }

    // the tool is the test assembly: the entry assembly of a test run is the test host, which does not target the framework of the plugins
    private static PluginHostOptions Options() => new("tool")
    {
        DefaultConfigFile = Path.Combine(Path.GetTempPath(), "bitwizards-plugin-tests", "none.json"),
        ToolAssembly = typeof(PluginInstallCommandTests).Assembly
    };

    [TestMethod]
    public async Task AnInstalledPackageIsLoadedByTheNextRun()
    {
        var plugins = Path.Combine(Folder("plugins"), "not-there-yet");
        var feed = FeedWithHelloPlugin();

        (await Tool(plugins, Options(), "hello")).Code.Should().NotBe(0, "there is no such command yet");
        var (code, _, error) = await Tool(plugins, Options(), "install", "--package", "Hello.Plugin", "--source", feed);

        code.Should().Be(0);
        error.Should().Contain("Package installed successfully.");
        File.Exists(Path.Combine(plugins, Hello + ".dll")).Should().BeTrue();
        (await Tool(plugins, Options(), "hello")).Output.Should().Be("hello from the plugin (hello)\n", "the plugin is loaded the next time");
    }

    [TestMethod]
    public async Task RefreshInstallsAsWellAndIsInTheHelp()
    {
        var plugins = Folder("plugins");
        var feed = FeedWithHelloPlugin();

        var (code, _, error) = await Tool(plugins, Options(), "install", "--package", "Hello.Plugin", "--source", feed, "--refresh");

        (code, error).Should().Match<(int Code, string Error)>(r => r.Code == 0 && r.Error.Contains("Package installed successfully."));
        (await Tool(plugins, Options(), "install", "--help")).Output.Should().Contain("--refresh").And.Contain("30 minutes");
    }

    [TestMethod]
    public async Task ThePackageCanBeGivenAsPkgAndSeveralSourcesCanFollowOneOption()
    {
        var plugins = Folder("plugins");
        var empty = Folder("empty-feed");
        var feed = FeedWithHelloPlugin("Hello.Plugin");

        var (code, _, error) = await Tool(plugins, Options(), "install", "--pkg", "Hello.Plugin", "--source", empty, feed);

        (code, error).Should().Match<(int Code, string Error)>(r => r.Code == 0 && r.Error.Contains("Package installed successfully."));
        File.Exists(Path.Combine(plugins, Hello + ".dll")).Should().BeTrue("the package is in the second source");
    }

    [TestMethod]
    public void PluginsRunOnTheFrameworkOfTheTool()
    {
        PluginInstaller.FrameworkOf(typeof(PluginHost).Assembly).Should().Be(NuGetFramework.Parse("net10.0"));
        PluginInstaller.FrameworkOf(null).Should().Be(NuGetFramework.Parse("net10.0"), "this library's own framework is the last resort");
    }

    [TestMethod]
    public async Task CleanEmptiesThePluginFolderFirst()
    {
        var plugins = Folder("plugins");
        File.WriteAllText(Path.Combine(plugins, "old.dll"), "left over");

        var (code, _, error) = await Tool(plugins, Options(), "install", "--package", "Hello.Plugin", "--source", FeedWithHelloPlugin(), "--clean");

        code.Should().Be(0);
        error.Should().Contain("Removed old.dll");
        Directory.GetFiles(plugins).Select(Path.GetFileName).Should().Equal(Hello + ".dll");
    }

    [TestMethod]
    public async Task APackageThatIsNotThereIsAFailure()
    {
        var (code, _, error) = await Tool(Folder("plugins"), Options(), "install", "--package", "Nothing.Here", "--source", FeedWithHelloPlugin());

        code.Should().Be(1);
        error.Should().Contain("Package Nothing.Here latest installation failed").And.Contain("Could not find any versions");
    }

    [TestMethod]
    public async Task ASourceThatIsNotConfiguredAndNotGivenIsAFailure()
    {
        var folder = Folder("nuget");
        File.WriteAllText(Path.Combine(folder, "nuget.config"), """<configuration><packageSources><clear /></packageSources></configuration>""");

        var (code, _, error) = await Tool(Folder("plugins"), Options(), "install", "--package", "Hello.Plugin", "--configfile", Path.Combine(folder, "nuget.config"));

        code.Should().Be(1);
        error.Should().Contain("No enabled NuGet package sources");
    }

    [TestMethod]
    public async Task TheInstallCommandCanBeLeftOut()
    {
        var options = new PluginHostOptions("tool") { InstallCommand = false, DefaultConfigFile = Options().DefaultConfigFile };

        var (code, _, _) = await Tool(Folder("plugins"), options, "install", "--package", "Hello.Plugin");

        code.Should().NotBe(0);
    }
}
