using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BitWizards.Plugins.Test;

[TestClass]
public class PluginHostTests
{
    private const string Hello = "BitWizards.Plugins.Test.HelloPlugin";
    private const string Broken = "BitWizards.Plugins.Test.BrokenPlugin";

    /// <summary>A new plugin folder with the sample plugins that were built for the tests.</summary>
    private static string Folder(params string[] plugins)
    {
        var folder = Path.Combine(Path.GetTempPath(), "bitwizards-plugin-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        foreach (var plugin in plugins)
            File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", plugin, plugin + ".dll"), Path.Combine(folder, plugin + ".dll"));
        return folder;
    }

    private static PluginHostOptions Options() => new("tool") { DefaultConfigFile = Path.Combine(Path.GetTempPath(), "bitwizards-plugin-tests", "none.json") };

    /// <summary>Starts a tool with the plugin folder, and runs a command line on it.</summary>
    private static async Task<(int Code, string Output, PluginHost Host)> Run(string folder, PluginHostOptions options, params string[] args)
    {
        string[] line = ["--plugins", folder, .. args];
        var host = PluginHost.Build(line, options, (services, _) => services.AddSingleton<IRootCommandConfigurator, ToolCommands>());
        var root = new RootCommand("tool");
        host.ConfigureRoot(root);
        var output = new StringWriter();
        var code = await host.InvokeAsync(root.Parse(line), new InvocationConfiguration { Output = output, Error = new StringWriter() });
        return (code, output.ToString().Replace("\r\n", "\n"), host);
    }

    /// <summary>The commands of the tool itself, which are registered before those of the plugins.</summary>
    private sealed class ToolCommands : IRootCommandConfigurator
    {
        public void ConfigureRootCommand(RootCommand rootCommand)
        {
            var own = new Command("own", "A command of the tool");
            own.SetAction(parse =>
            {
                parse.InvocationConfiguration.Output.WriteLine("the tool");
                return 0;
            });
            rootCommand.Subcommands.Add(own);
        }
    }

    [TestMethod]
    public async Task APluginAddsServicesAndCommandsToTheTool()
    {
        var folder = Folder(Hello);

        var (code, output, host) = await Run(folder, Options(), "hello");
        using var _ = host;

        (code, output).Should().Be((0, "hello from the plugin (hello)\n"));
        host.Catalog.Warnings.Should().BeEmpty();
        host.Catalog.Plugins.Should().ContainSingle().Which.Should().Match<PluginInfo>(p =>
            p.Status == PluginStatus.Loaded && p.AssemblyName == Hello && p.Modules.SequenceEqual(new[] { Hello + ".HelloModule" }));
        (await Run(folder, Options(), "own")).Output.Should().Be("the tool\n", "the commands of the tool are there as well");
    }

    [TestMethod]
    public async Task ConfigurationIsBuiltFromFilesValuesAndModules()
    {
        var folder = Folder(Hello);
        var file = Path.Combine(folder, "config.json");
        File.WriteAllText(file, """{ "Hello": { "Greeting": "hi" } }""");

        (await Run(folder, Options(), "--config", file, "hello")).Output.Should().Be("hi from the plugin (hello)\n", "a file, and a value that the module adds");
        (await Run(folder, Options(), "--config", file, "--set", "Hello:Greeting=hey", "hello")).Output.Should().StartWith("hey from", "--set wins over the files");
        (await Run(folder, Options(), "hello")).Output.Should().StartWith("hello from", "without configuration there is the default");
    }

    [TestMethod]
    public async Task TheDefaultConfigurationFileIsOptional()
    {
        var folder = Folder(Hello);
        var file = Path.Combine(folder, "tool.json");
        File.WriteAllText(file, """{ "Hello": { "Greeting": "from the default file" } }""");

        (await Run(folder, new PluginHostOptions("tool") { DefaultConfigFile = file }, "hello")).Output.Should().StartWith("from the default file");
        (await Run(folder, new PluginHostOptions("tool") { DefaultConfigFile = Path.Combine(folder, "gone.json") }, "hello")).Code.Should().Be(0);
    }

    [TestMethod]
    public async Task AFolderThatIsNotThereHasNoPlugins()
    {
        var (code, output, host) = await Run(Path.Combine(Path.GetTempPath(), "bitwizards-plugin-tests", "nothing-" + Guid.NewGuid().ToString("N")), Options(), "plugins", "list");
        using var _ = host;

        code.Should().Be(0);
        output.Should().Contain("No plugin assemblies found.");
        host.Catalog.Plugins.Should().BeEmpty();
        host.Catalog.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AModuleThatFailsIsAWarningAndTheToolGoesOn()
    {
        var (code, output, host) = await Run(Folder(Hello, Broken), Options(), "hello");
        using var _ = host;

        (code, output).Should().Be((0, "hello from the plugin (hello)\n"));
        host.Catalog.Warnings.Should().ContainSingle().Which.Should().Contain("BrokenModule").And.Contain("this module does not work");
    }

    [TestMethod]
    public async Task AFileThatIsNoAssemblyIsAWarning()
    {
        var folder = Folder(Hello);
        File.WriteAllText(Path.Combine(folder, "garbage.dll"), "not an assembly");

        var (code, _, host) = await Run(folder, Options(), "hello");
        using var _ = host;

        code.Should().Be(0);
        host.Catalog.Warnings.Should().ContainSingle().Which.Should().Contain("garbage.dll");
        host.Catalog.Plugins.Should().Contain(p => p.Status == PluginStatus.NotLoaded && p.File.EndsWith("garbage.dll"));
    }

    [TestMethod]
    public async Task WhatTheToolHasWinsOverACopyInTheFolder()
    {
        var folder = Folder(Hello);
        var own = typeof(IPluginModule).Assembly.Location;
        File.Copy(own, Path.Combine(folder, Path.GetFileName(own)));

        var (code, _, host) = await Run(folder, Options(), "hello");
        using var _ = host;

        code.Should().Be(0);
        host.Catalog.Plugins.Should().ContainSingle().Which.AssemblyName.Should().Be(Hello, "the copy of the library is the tool's");
        host.Catalog.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    public async Task APluginForAnotherMajorVersionOfTheContractIsSkipped()
    {
        var options = Options();
        options.Contracts.Clear();
        options.Contracts.Add(new AssemblyName("BitWizards.Plugins") { Version = new Version(9, 0, 0, 0) });

        var (_, output, host) = await Run(Folder(Hello), options, "plugins", "list");
        using var _ = host;

        host.Catalog.Plugins.Should().ContainSingle().Which.Status.Should().Be(PluginStatus.IncompatibleContract);
        host.Catalog.Warnings.Should().ContainSingle().Which.Should().Contain("skipped");
        output.Should().Contain("[incompatible contract: BitWizards.Plugins 0.1.0.0]");
        (await Run(Folder(Hello), options, "hello")).Host.Catalog.Plugins.Should().OnlyContain(p => p.Modules.Length == 0);
    }

    [TestMethod]
    public async Task PluginsListTellsWhatWasFound()
    {
        var (_, output, host) = await Run(Folder(Hello, Broken), Options(), "plugins", "list");
        using var _ = host;

        output.Should().Contain($"{Hello} 0.1.0.0 [loaded] ({Hello}.dll)").And.Contain($"  module: {Hello}.HelloModule")
            .And.Contain($"{Broken} 0.1.0.0 [loaded]");
    }

    [TestMethod]
    public async Task ThePluginsCommandCanBeLeftOut()
    {
        var options = new PluginHostOptions("tool") { PluginsCommand = false, DefaultConfigFile = Options().DefaultConfigFile };

        var (code, _, host) = await Run(Folder(Hello), options, "plugins", "list");
        using var _ = host;

        code.Should().NotBe(0, "there is no such command");
    }

    [TestMethod]
    public void TheFoldersFollowTheNameOfTheTool()
    {
        PluginDirectories.NextToTool("ks").Should().EndWith("ks-plugins").And.StartWith(AppContext.BaseDirectory);
        PluginDirectories.PerUser("ks").Should().Contain("ks").And.EndWith("plugins");
        new PluginHostOptions("ks").PluginDirectoryOrDefault.Should().Be(PluginDirectories.NextToTool("ks"));
        new PluginHostOptions("ks") { DefaultPluginDirectory = "/x" }.PluginDirectoryOrDefault.Should().Be("/x");
        new PluginHostOptions("ks").ConfigFileOrDefault.Should().Be("ks.json");
    }

    [TestMethod]
    public void AModuleMustBeAModule()
    {
        new PluginModuleAttribute(typeof(string)).Invoking(a => a.CreateModule()).Should().Throw<InvalidOperationException>().WithMessage("*not an IPluginModule*");
        new PluginModuleAttribute<NothingModule>().CreateModule().Should().BeOfType<NothingModule>();
    }

    private sealed class NothingModule : PluginModule;

    [TestMethod]
    public async Task TheContextIsThereAcrossAwaitsAndGoneAfterwards()
    {
        var holder = new ContextHolder<string>();
        holder.HasContext.Should().BeFalse();

        var seen = await holder.RunWithContext("here", async () =>
        {
            await Task.Delay(10);
            return holder.Context;
        });

        seen.Should().Be("here");
        holder.HasContext.Should().BeFalse();
        holder.Context.Should().BeNull();
    }
}
