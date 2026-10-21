using System.CommandLine;

namespace BitWizards.Plugins;

/// <summary>The <c>plugins list</c> command: the assemblies of the plugin folder, and what became of them.</summary>
internal sealed class PluginsCommandConfigurator(PluginCatalog catalog) : IRootCommandConfigurator
{
    public void ConfigureRootCommand(RootCommand rootCommand)
    {
        var list = new Command("list", "List the contents of the plugin folder and the modules that were loaded");
        list.SetAction(parse =>
        {
            Write(catalog, parse.InvocationConfiguration.Output);
            return 0;
        });
        rootCommand.Subcommands.Add(new Command("plugins", "Inspect the plugins") { Subcommands = { list } });
    }

    internal static void Write(PluginCatalog catalog, TextWriter writer)
    {
        writer.WriteLine($"Plugin folder: {catalog.Directory}");
        if (catalog.Plugins.Count == 0)
            writer.WriteLine("No plugin assemblies found.");
        foreach (var plugin in catalog.Plugins)
        {
            var location = Path.GetRelativePath(catalog.Directory, plugin.File);
            var status = plugin.Detail is null ? Describe(plugin.Status) : $"{Describe(plugin.Status)}: {plugin.Detail}";
            writer.WriteLine($"{plugin.AssemblyName ?? Path.GetFileNameWithoutExtension(plugin.File)} {plugin.AssemblyVersion} [{status}] ({location})");
            foreach (var module in plugin.Modules)
                writer.WriteLine($"  module: {module}");
        }
    }

    private static string Describe(PluginStatus status)
        => status switch
        {
            PluginStatus.Loaded => "loaded",
            PluginStatus.NoModule => "no module",
            PluginStatus.IncompatibleContract => "incompatible contract",
            PluginStatus.NotLoaded => "not loaded",
            _ => "failed"
        };
}
