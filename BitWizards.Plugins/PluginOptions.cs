using System.CommandLine;

namespace BitWizards.Plugins;

/// <summary>
/// The options that every command of a tool with plugins understands. A first parse reads them before the plugins are loaded
/// (they decide which plugins, and with which configuration), and they are declared on the root command for the help.
/// </summary>
public sealed class PluginOptions
{
    public Option<string?> Plugins { get; } = new("--plugins", "-p")
    {
        Description = "The folder with the plugins (default: for the tool).",
        Recursive = true
    };

    public Option<string[]> Config { get; } = new("--config", "-c")
    {
        Description = "JSON configuration file. Repeat it for more.",
        Recursive = true
    };

    public Option<string[]> Set { get; } = new("--set")
    {
        Description = "A configuration value as key=value. Repeat it for more.",
        Recursive = true
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Plugins);
        command.Options.Add(Config);
        command.Options.Add(Set);
    }

    /// <summary>The options as the command line has them, read without the commands and options of the tool.</summary>
    internal ParseResult Read(string[] args)
    {
        var bootstrap = new RootCommand();
        AddTo(bootstrap);
        return bootstrap.Parse(args);
    }
}
