using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BitWizards.Plugins;

/// <summary>
/// A tool with plugins, started: the plugins are loaded, and the container has the services of the tool and of its plugins.
/// </summary>
/// <remarks>
/// Starting is in two phases. A first parse of the command line reads only <c>--plugins</c>, <c>--config</c> and <c>--set</c>; with those the
/// plugins are loaded, the configuration is built (the files, the values of <c>--set</c>, and what the modules add), and the services are registered:
/// first those of the tool, then those of the modules, which can replace them.
/// </remarks>
public sealed class PluginHost : IDisposable
{
    private PluginHost(ServiceProvider services, PluginCatalog catalog, IConfigurationRoot configuration, PluginOptions options)
    {
        Services = services;
        Catalog = catalog;
        Configuration = configuration;
        Options = options;
    }

    public ServiceProvider Services { get; }

    /// <summary>What was found in the folder of the plugins. A tool writes its <see cref="PluginCatalog.Warnings"/> to its error output.</summary>
    public PluginCatalog Catalog { get; }

    public IConfigurationRoot Configuration { get; }

    /// <summary>The options of every command, to declare on the root command (<see cref="ConfigureRoot"/> does).</summary>
    public PluginOptions Options { get; }

    /// <param name="args">The command line of the tool.</param>
    /// <param name="configureServices">Registers the services of the tool itself, with the configuration.</param>
    public static PluginHost Build(string[] args, PluginHostOptions options, Action<IServiceCollection, IConfiguration>? configureServices = null)
    {
        var pluginOptions = new PluginOptions();
        var parse = pluginOptions.Read(args);
        var directory = parse.GetValue(pluginOptions.Plugins) is { } given ? Path.GetFullPath(given) : options.PluginDirectoryOrDefault;

        var catalog = new PluginCatalog { Directory = directory };
        var modules = PluginLoader.LoadModules(directory, options, catalog).ToList();

        var builder = new ConfigurationBuilder();
        var files = parse.GetValue(pluginOptions.Config) ?? [];
        foreach (var file in files)
            builder.AddJsonFile(Path.GetFullPath(file), optional: false);
        if (files.Length == 0)
            builder.AddJsonFile(Path.GetFullPath(options.ConfigFileOrDefault), optional: true);
        builder.AddInMemoryCollection((parse.GetValue(pluginOptions.Set) ?? []).Select(Pair));
        modules = modules.Where(m => Guard(m, "configure", catalog, () => m.Configure(builder))).ToList();
        var configuration = builder.Build();

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(catalog);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(configuration);
        services.AddContext<ParseResult>();
        services.AddContext<Command>();
        if (options.PluginsCommand)
            services.AddSingleton<IRootCommandConfigurator, PluginsCommandConfigurator>();
        configureServices?.Invoke(services, configuration);
        foreach (var module in modules)
            Guard(module, "register services", catalog, () => module.RegisterServices(configuration, services));

        return new PluginHost(services.BuildServiceProvider(), catalog, configuration, pluginOptions);
    }

    /// <summary>Declares the options of every command on the root command, and lets every <see cref="IRootCommandConfigurator"/> add its commands.</summary>
    public void ConfigureRoot(RootCommand root)
    {
        Options.AddTo(root);
        foreach (var configurator in Services.GetServices<IRootCommandConfigurator>())
            configurator.ConfigureRootCommand(root);
    }

    /// <summary>Invokes the parsed command line, with its <c>ParseResult</c> and <c>Command</c> available as <see cref="IContext{T}"/>.</summary>
    public Task<int> InvokeAsync(ParseResult parse, InvocationConfiguration? configuration = null, CancellationToken cancellationToken = default)
        => Services.GetRequiredService<IContext<ParseResult>>().RunWithContext(parse, () =>
            Services.GetRequiredService<IContext<Command>>().RunWithContext(parse.CommandResult.Command, () =>
                parse.InvokeAsync(configuration, cancellationToken)));

    public void Dispose() => Services.Dispose();

    /// <summary>A module that fails (on a dependency it misses, say) is a warning and is left out: the tool still runs.</summary>
    private static bool Guard(IPluginModule module, string what, PluginCatalog catalog, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            catalog.Warnings.Add($"Module '{module.GetType().FullName}' failed to {what}: {ex.Message}");
            return false;
        }
    }

    private static KeyValuePair<string, string?> Pair(string text)
        => text.IndexOf('=') is var i and >= 0 ? new(text[..i], text[(i + 1)..]) : new(text, "");
}
