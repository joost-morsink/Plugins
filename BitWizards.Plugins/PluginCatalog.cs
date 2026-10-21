namespace BitWizards.Plugins;

public enum PluginStatus
{
    /// <summary>The assembly was loaded and its modules were created.</summary>
    Loaded,
    /// <summary>The assembly has no <see cref="PluginModuleAttribute"/>: it is a dependency of a plugin, say.</summary>
    NoModule,
    /// <summary>The assembly is built against another major version of a contract than the tool has.</summary>
    IncompatibleContract,
    /// <summary>The file could not be loaded as an assembly.</summary>
    NotLoaded,
    /// <summary>The assembly was loaded, but its modules could not be created.</summary>
    Failed
}

/// <param name="Detail">More about why a plugin is not loaded.</param>
public sealed record PluginInfo(string File, string? AssemblyName, Version? AssemblyVersion, string[] Modules, PluginStatus Status, string? Detail = null);

/// <summary>What the loader found: the assemblies of the plugin folder and what became of them, and what went wrong.</summary>
public sealed class PluginCatalog
{
    public string Directory { get; init; } = "";
    public List<PluginInfo> Plugins { get; } = [];

    /// <summary>Problems that did not stop the tool: a plugin that was skipped or a module that failed. A tool writes them to its error output.</summary>
    public List<string> Warnings { get; } = [];
}
