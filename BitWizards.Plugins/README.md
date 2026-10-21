# BitWizards.Plugins

Plugins for command line tools. A plugin is a folder of assemblies; each declares a *module* that adds configuration, services and commands to the tool.
It is the plugin system of `giki`, `archvalidator` and `ks` in one place: loading the plugins of a folder, and installing them there from NuGet feeds.

Dependencies: `System.CommandLine`, `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Configuration.Json` for the tool, and
`NuGet.Protocol` (with the NuGet libraries it needs) for `install`.

## In a tool

```csharp
var options = new PluginHostOptions("mytool");
using var host = PluginHost.Build(args, options, (services, configuration) =>
{
    services.AddTransient<IRootCommandConfigurator, MyCommands>();   // the commands of the tool itself
});

foreach (var warning in host.Catalog.Warnings)
    Console.Error.WriteLine($"Warning: {warning}");

var root = new RootCommand("My tool");
host.ConfigureRoot(root);                  // the options of the plugins, the commands of the tool and of its plugins
return await host.InvokeAsync(root.Parse(args));
```

`PluginHost.Build` does the starting in two phases. A first parse reads only `--plugins`, `--config` and `--set`; with those it loads the plugins,
builds the configuration (the `--config` files or an optional `mytool.json`, then `--set key=value`, then what the modules add), and registers the services:
those of the tool first, then those of the modules, which can replace them. A module that fails is a warning in `host.Catalog.Warnings`, not a failure.

`mytool plugins list` shows what was found in the plugin folder (turn it off with `PluginHostOptions.PluginsCommand`).

A module that is part of the tool itself is registered like that of a plugin with `services.AddPluginModule<MyBuiltInModule>(configuration)`.

`--config`, `--set` (`-s`) and `install --source` take more than one value (`--set a=1 b=2`, or repeat the option). For that, **put them after the command**:
before it, `mytool --set a=1 run` takes `run` for a value.

| | |
|---|---|
| Plugin folder | `--plugins <folder>`, or `PluginHostOptions.DefaultPluginDirectory`: `mytool-plugins` next to the tool (`PluginDirectories.NextToTool`, which an update of the tool resets) or in the data folder of the user (`PluginDirectories.PerUser`) |
| Contract | the assemblies that plugins are built against (`PluginHostOptions.Contracts`, this library is there already): a plugin that references another major version is skipped with a warning |
| Module discovery | a tool whose plugins declare their modules with an attribute of its own (so they do not reference this library) adds a function to `PluginHostOptions.ModuleDiscovery` that turns what an assembly declares into `IPluginModule`s, such as by wrapping them. It is consulted for every assembly, next to `[assembly: PluginModule]`; an assembly that yields a module by either is a plugin. A function that throws fails that plugin, with a warning |
| Scope | `host.Scope` is the scope of the run: the commands, and what they use, are resolved from it. Register a `IRootCommandConfigurator` (transient) so that it is |
| Context | `IContext<ParseResult>` and `IContext<Command>` give what is resolved from `host.Scope` the command line that is being invoked. `IContext<T>` is the one of `giki` (`RunWithContext`, `RunWithContextAsync`, `IContext<T>.Create(...)`, `services.AddContext<T>(defaultValue)`): scoped, set explicitly for the code that runs with it, and put back afterwards. Use the async form for async code: a sync run ends at the first `await`. Code that runs at the same time in one scope shares the context, so give it a scope of its own |

### Loading

All assemblies in the plugin folder (and below it) are loaded into the default load context, so a type that the tool and a plugin share is one type.
For that, **what the tool has wins**: a copy of an assembly of the tool or of the shared framework in the plugin folder is ignored. A plugin brings its own dependencies,
and only those.

### Installing plugins

`mytool install --package <id> [--version <v>|latest] [--source <feed>...]` installs a NuGet package into the plugin folder, where the next run of the tool loads it.
Turn it off with `PluginHostOptions.InstallCommand`; `PluginInstaller`, `NuGetFeeds` and `HostAssemblies` (namespace `BitWizards.Plugins.Installation`) are there for a tool that does it itself.

- **Walks the dependency graph.** Each package is downloaded from the first feed that has it, and its managed assemblies for the framework of the tool are copied
  (the `TargetFramework` of `PluginHostOptions.ToolAssembly`, such as `net10.0`, or the nearest older one in the package). A dependency resolves to the lowest version that satisfies its range, as NuGet restore does. Native assets are reported, not installed.
- **Queries all the sources of NuGet.** Feeds come from the `nuget.config` files (the working directory upwards, then the user and the machine), with their credentials,
  `<clear />`, disabled sources and *package source mapping*; `--source` (a name from the configuration, a URL or a folder) or `--configfile` replace them. The list of versions is never cached.
- **Does not install what the tool has.** `HostAssemblies` knows the framework, the assemblies next to the tool and (from its `deps.json`) the packages of the tool; those are skipped,
  with their dependencies. A plugin assembly with a *higher assembly version* than the tool's copy fails the install, since it would fail at load time: all such conflicts are reported at once.
  `--include-provided` installs everything.
- **Prereleases** are only considered with `--prerelease`, or when the tool itself is a prerelease (`PluginHostOptions.ToolAssembly`, the entry assembly by default). A dependency range that names a prerelease allows it.
- **`--clean`** empties the plugin folder first, and refuses when the folder is a root, or contains the tool, the working directory or the home folder.

A plugin can only use assemblies that are no newer than those of the tool. A tool that runs plugins which need newer framework libraries references those versions itself.

## In a plugin

Reference `BitWizards.Plugins` (and whatever contract the tool offers), and declare a module:

```csharp
[assembly: PluginModule<MyModule>]

public class MyModule : PluginModule
{
    public override void RegisterServices(IConfiguration configuration, IServiceCollection services)
        => services.AddTransient<IRootCommandConfigurator, HelloCommands>();
}
```

Copy the assemblies of the plugin, with its dependencies, to the plugin folder. Set `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`
in the project of the plugin to have its build output hold them.

## Packages

The packages are on GitHub Packages (`https://nuget.pkg.github.com/joost-morsink/index.json`); add the source with a token that can read packages.
A tag `v<version>` (the version in `Directory.Build.props`) publishes them.
