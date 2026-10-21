using System.CommandLine;

namespace BitWizards.Plugins;

/// <summary>
/// Contributes commands (or options) to the root command of the tool. Register it as a service in a module:
/// <c>services.AddSingleton&lt;IRootCommandConfigurator, MyCommands&gt;()</c>.
/// </summary>
public interface IRootCommandConfigurator
{
    void ConfigureRootCommand(RootCommand rootCommand);
}
