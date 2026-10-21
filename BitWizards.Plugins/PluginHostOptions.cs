using System.Reflection;

namespace BitWizards.Plugins;

/// <summary>What a tool tells about itself to host plugins.</summary>
public sealed class PluginHostOptions
{
    /// <param name="toolName">The name of the tool, such as <c>ks</c>: it names the plugin folder and the configuration file.</param>
    public PluginHostOptions(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ToolName = toolName;
    }

    public string ToolName { get; }

    /// <summary>
    /// The folder with the plugins when <c>--plugins</c> is not given: <c>&lt;tool&gt;-plugins</c> next to the tool by default
    /// (<see cref="PluginDirectories.NextToTool"/>), which an update of the tool resets; <see cref="PluginDirectories.PerUser"/> does not.
    /// </summary>
    public string DefaultPluginDirectory { get; init; } = "";

    /// <summary>The configuration file that is used when <c>--config</c> is not given, if it is there: <c>&lt;tool&gt;.json</c> in the current directory.</summary>
    public string DefaultConfigFile { get; init; } = "";

    /// <summary>Whether the <c>plugins list</c> command is added to the tool.</summary>
    public bool PluginsCommand { get; init; } = true;

    /// <summary>Whether the <c>install</c> command is added to the tool: it installs a NuGet package, with its dependencies, into the plugin folder.</summary>
    public bool InstallCommand { get; init; } = true;

    /// <summary>
    /// The assembly of the tool (default: the entry assembly). Its <c>deps.json</c> tells which packages the tool has, which <c>install</c> does not install again,
    /// and its version tells whether the tool is a prerelease, which allows prerelease packages.
    /// </summary>
    public Assembly? ToolAssembly { get; init; }

    /// <summary>
    /// The assemblies that plugins are built against, whose major version they must have: a plugin that references another major version is skipped.
    /// The assembly of this library is there already; a tool adds its own contract (<c>Contracts.Add(typeof(IKsCommand).Assembly.GetName())</c>).
    /// </summary>
    public IList<AssemblyName> Contracts { get; } = [typeof(IPluginModule).Assembly.GetName()];

    /// <summary>
    /// Ways to find modules in an assembly other than <see cref="PluginModuleAttribute"/>, for a tool whose plugins declare their modules with an attribute of its own
    /// (one that does not reference this library): each function turns what an assembly declares into modules, such as by wrapping them. They are consulted
    /// for every assembly of the plugin folder, in addition to <see cref="PluginModuleAttribute"/>; an assembly that yields a module by either is a plugin.
    /// A function that throws makes that plugin fail, with a warning, as a module that can not be created does.
    /// </summary>
    public IList<Func<Assembly, IEnumerable<IPluginModule>>> ModuleDiscovery { get; } = [];

    internal string PluginDirectoryOrDefault => DefaultPluginDirectory is { Length: > 0 } directory ? directory : PluginDirectories.NextToTool(ToolName);

    internal string ConfigFileOrDefault => DefaultConfigFile is { Length: > 0 } file ? file : $"{ToolName}.json";
}

/// <summary>Where the plugins of a tool can be.</summary>
public static class PluginDirectories
{
    /// <summary><c>&lt;tool&gt;-plugins</c> in the folder of the tool. It is lost when the tool is updated.</summary>
    public static string NextToTool(string toolName) => Path.Combine(AppContext.BaseDirectory, $"{toolName}-plugins");

    /// <summary><c>plugins</c> in the data folder of the tool for the user (<c>~/.local/share/&lt;tool&gt;</c>, <c>%LOCALAPPDATA%\&lt;tool&gt;</c>).</summary>
    public static string PerUser(string toolName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), toolName, "plugins");
}
