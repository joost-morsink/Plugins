# BitWizards.Plugins

Plugins for command line tools. A plugin is a folder of assemblies; each declares a *module* that adds configuration, services and commands to the tool.
It is the plugin system of `giki`, `archvalidator` and `ks` in one place, with three dependencies: `System.CommandLine`, `Microsoft.Extensions.DependencyInjection`
and `Microsoft.Extensions.Configuration.Json`.

## In a tool

```csharp
var options = new PluginHostOptions("mytool");
using var host = PluginHost.Build(args, options, (services, configuration) =>
{
    services.AddSingleton<IRootCommandConfigurator, MyCommands>();   // the commands of the tool itself
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

| | |
|---|---|
| Plugin folder | `--plugins <folder>`, or `PluginHostOptions.DefaultPluginDirectory`: `mytool-plugins` next to the tool (`PluginDirectories.NextToTool`, which an update of the tool resets) or in the data folder of the user (`PluginDirectories.PerUser`) |
| Contract | the assemblies that plugins are built against (`PluginHostOptions.Contracts`, this library is there already): a plugin that references another major version is skipped with a warning |
| Context | `IContext<ParseResult>` and `IContext<Command>` give the services of the container the command line that is being invoked |

### Loading

All assemblies in the plugin folder (and below it) are loaded into the default load context, so a type that the tool and a plugin share is one type.
For that, **what the tool has wins**: a copy of an assembly of the tool or of the shared framework in the plugin folder is ignored. A plugin brings its own dependencies,
and only those.

## In a plugin

Reference `BitWizards.Plugins` (and whatever contract the tool offers), and declare a module:

```csharp
[assembly: PluginModule<MyModule>]

public class MyModule : PluginModule
{
    public override void RegisterServices(IConfiguration configuration, IServiceCollection services)
        => services.AddSingleton<IRootCommandConfigurator, HelloCommands>();
}
```

Copy the assemblies of the plugin, with its dependencies, to the plugin folder. Set `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`
in the project of the plugin to have its build output hold them.

## Packages

The packages are on GitHub Packages (`https://nuget.pkg.github.com/joost-morsink/index.json`); add the source with a token that can read packages.
A tag `v<version>` (the version in `Directory.Build.props`) publishes them.
