using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using System.Runtime.Loader;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
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

    private static PluginHostOptions Options() => new("tool")
    {
        DefaultConfigFile = Path.Combine(Path.GetTempPath(), "bitwizards-plugin-tests", "none.json"),
        ToolAssembly = typeof(PluginHostTests).Assembly
    };

    /// <summary>Starts a tool with the plugin folder, and runs a command line on it.</summary>
    private static async Task<(int Code, string Output, PluginHost Host)> Run(string folder, PluginHostOptions options, params string[] args)
    {
        string[] line = ["--plugins", folder, .. args];
        var host = PluginHost.Build(line, options, (services, _) => services.AddTransient<IRootCommandConfigurator, ToolCommands>());
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

        // the options follow the command: an option takes more than one value, so a command after it would be taken for one
        (await Run(folder, Options(), "hello", "--config", file)).Output.Should().Be("hi from the plugin (hello)\n", "a file, and a value that the module adds");
        (await Run(folder, Options(), "hello", "--config", file, "--set", "Hello:Greeting=hey")).Output.Should().StartWith("hey from", "--set wins over the files");
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

    private const string Foreign = "BitWizards.Plugins.Test.ForeignPlugin";

    /// <summary>What a tool with a module contract of its own does: wraps the modules that its plugins declare, which do not know this library.</summary>
    private static IEnumerable<IPluginModule> ForeignModules(Assembly assembly)
        => assembly.GetCustomAttributes().Where(a => a.GetType().Name == "ForeignModuleAttribute")
            .Select(a => new ForeignAdapter(Activator.CreateInstance((Type)a.GetType().GetProperty("ModuleType")!.GetValue(a)!)!));

    private sealed class ForeignAdapter(object module) : IPluginModuleWrapper
    {
        public object Module => module;

        public void Configure(IConfigurationBuilder configurationBuilder)
        {
        }

        public void RegisterServices(IConfiguration configuration, IServiceCollection services)
            => module.GetType().GetMethod("RegisterServices")!.Invoke(module, [configuration, services]);
    }

    [TestMethod]
    public async Task ModulesOfAnotherContractAreFoundThroughModuleDiscovery()
    {
        var options = Options();
        options.ModuleDiscovery.Add(ForeignModules);

        var (_, _, host) = await Run(Folder(Foreign, Hello), options, "hello", "--set", "Foreign:Value=found");
        using var _ = host;

        host.Catalog.Warnings.Should().BeEmpty();
        host.Catalog.Plugins.Should().HaveCount(2).And.OnlyContain(p => p.Status == PluginStatus.Loaded && p.Modules.Length == 1);
        host.Catalog.Plugins.Single(p => p.AssemblyName == Foreign).Modules.Should().ContainSingle().Which.Should().Be(Foreign + ".ForeignModule", "a wrapper is named after the module it wraps");
        var marker = AssemblyLoadContext.Default.Assemblies.Single(a => a.GetName().Name == Foreign).GetType(Foreign + ".ForeignMarker")!;
        marker.GetProperty("Value")!.GetValue(host.Services.GetRequiredService(marker)).Should().Be("found");
    }

    [TestMethod]
    public async Task WithoutModuleDiscoveryAPluginOfAnotherContractHasNoModule()
    {
        var (_, _, host) = await Run(Folder(Foreign), Options(), "plugins", "list");
        using var _ = host;

        host.Catalog.Plugins.Should().ContainSingle().Which.Status.Should().Be(PluginStatus.NoModule);
        host.Catalog.Warnings.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ModuleDiscoveryThatThrowsFailsThatPluginOnly()
    {
        var options = Options();
        options.ModuleDiscovery.Add(assembly => assembly.GetName().Name == Foreign
            ? throw new InvalidOperationException("cannot discover")
            : []);

        var (code, output, host) = await Run(Folder(Foreign, Hello), options, "hello");
        using var _ = host;

        (code, output).Should().Be((0, "hello from the plugin (hello)\n"), "the other plugin is still loaded");
        host.Catalog.Plugins.Should().Contain(p => p.AssemblyName == Foreign && p.Status == PluginStatus.Failed)
            .And.Contain(p => p.AssemblyName == Hello && p.Status == PluginStatus.Loaded);
        host.Catalog.Warnings.Should().ContainSingle().Which.Should().Contain("cannot discover");
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

        var seen = await holder.RunWithContextAsync("here", async () =>
        {
            await Task.Delay(10);
            return holder.Context;
        });

        seen.Should().Be("here");
        holder.HasContext.Should().BeFalse();
        holder.Context.Should().BeNull();
    }

    [TestMethod]
    public async Task TheDeveloperSaysWhereTheContextEnds()
    {
        var holder = new ContextHolder<string>();

        // a context that is run with ends where the code that is run ends: for an async lambda that is at its first await, unless the async form is used
        var afterTheAwait = await holder.RunWithContext("sync", async () =>
        {
            await Task.Yield();
            return holder.Context;
        });
        var insideAsync = await holder.RunWithContextAsync("async", async () =>
        {
            await Task.Yield();
            return holder.Context;
        });

        (afterTheAwait, insideAsync).Should().Be((null, "async"));
    }

    [TestMethod]
    public async Task TheContextHasAllTheWaysToRunWithIt()
    {
        var holder = IContext<string>.Create();
        var seen = new List<string?>();

        holder.RunWithContext("action", () => seen.Add(holder.Context));
        await holder.RunWithContextAsync("task", async () =>
        {
            await Task.Yield();
            seen.Add(holder.Context);
        });
        seen.Add(await holder.RunWithContextAsync("task of a value", async () =>
        {
            await Task.Yield();
            return holder.Context;
        }));
        seen.Add(holder.RunWithContext("value", () => holder.Context));
        holder.RunWithContext("outer", () => holder.RunWithContext("inner", () => seen.Add(holder.Context)));

        seen.Should().Equal("action", "task", "task of a value", "value", "inner");
        holder.HasContext.Should().BeFalse("it is gone afterwards, also after nesting");
        IContext.Create("given").Context.Should().Be("given");
        IContext<string>.Create("given").HasContext.Should().BeTrue();
    }

    [TestMethod]
    public async Task ADefaultContextIsThereOutsideARun()
    {
        var services = new ServiceCollection().AddContext("default").AddContext<int>();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var text = scope.ServiceProvider.GetRequiredService<IContext<string>>();

        (text.HasContext, text.Context).Should().Be((true, "default"));
        text.RunWithContext("run", () => text.Context).Should().Be("run");
        text.Context.Should().Be("default", "back to the default afterwards");
        scope.ServiceProvider.GetRequiredService<IContext<string>>().Should().BeSameAs(text);
        scope.ServiceProvider.GetRequiredService<IContext<int>>().HasContext.Should().BeTrue("a value type always has a value, as in giki; it is 0 here");
    }

    [TestMethod]
    public async Task EachScopeHasItsOwnContext()
    {
        await using var provider = new ServiceCollection().AddContext<string>().BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var (one, two) = (first.ServiceProvider.GetRequiredService<IContext<string>>(), second.ServiceProvider.GetRequiredService<IContext<string>>());

        var inSecond = await one.RunWithContextAsync("one", async () =>
        {
            await Task.Yield();
            return (one.Context, two.Context);
        });

        inSecond.Should().Be(("one", null), "the context of the other scope is not touched");
        one.Should().NotBeSameAs(two);
    }

    [TestMethod]
    public async Task ACommandOfThePluginSeesTheContextOfTheScopeOfTheRun()
    {
        // HelloCommands takes IContext<Command> and is registered transient: it gets the one of the scope that the host resolves it from
        var (_, output, host) = await Run(Folder(Hello), Options(), "hello");
        using var _ = host;

        output.Should().Be("hello from the plugin (hello)\n");
        host.Scope.ServiceProvider.GetRequiredService<IContext<System.CommandLine.Command>>().HasContext.Should().BeFalse("afterwards it is gone from the scope");
    }

    private sealed class GreetingModule : PluginModule
    {
        public override void RegisterServices(IConfiguration configuration, IServiceCollection services)
            => services.AddSingleton(new Greeting(configuration["Greeting"] ?? "none"));
    }

    private sealed record Greeting(string Text);

    [TestMethod]
    public void AModuleOfTheToolItselfIsRegisteredLikeThatOfAPlugin()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Greeting"] = "hi" }).Build();

        using var provider = new ServiceCollection().AddPluginModule<GreetingModule>(configuration).BuildServiceProvider();

        provider.GetRequiredService<Greeting>().Text.Should().Be("hi");
    }

    [TestMethod]
    public void MoreThanOneConfigurationValueOrFileCanFollowAnOption()
    {
        var folder = Folder();
        var (first, second) = (Path.Combine(folder, "one.json"), Path.Combine(folder, "two.json"));
        File.WriteAllText(first, """{ "File": "one", "Both": "one" }""");
        File.WriteAllText(second, """{ "Both": "two" }""");

        using var host = PluginHost.Build(["--plugins", folder, "run", "--config", first, second, "--set", "A=1", "B=2", "-s", "C=3", "--set", "D=4"], Options());

        var configuration = host.Configuration;
        (configuration["File"], configuration["Both"]).Should().Be(("one", "two"), "the files, the later one wins");
        new[] { "A", "B", "C", "D" }.Select(key => configuration[key]).Should().Equal("1", "2", "3", "4");
    }
}
