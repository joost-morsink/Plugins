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

    /// <summary>
    /// The assemblies that plugins are built against, whose major version they must have: a plugin that references another major version is skipped.
    /// The assembly of this library is there already; a tool adds its own contract (<c>Contracts.Add(typeof(IKsCommand).Assembly.GetName())</c>).
    /// </summary>
    public IList<AssemblyName> Contracts { get; } = [typeof(IPluginModule).Assembly.GetName()];

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
