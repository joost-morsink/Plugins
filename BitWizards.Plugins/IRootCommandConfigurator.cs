using System.CommandLine;

namespace BitWizards.Plugins;

/// <summary>
/// Contributes commands (or options) to the root command of the tool. Register it as a service in a module:
/// <c>services.AddTransient&lt;IRootCommandConfigurator, MyCommands&gt;()</c>.
/// It is resolved from the scope of the run (<see cref="PluginHost.Scope"/>), so it can take an <see cref="IContext{T}"/>: as a singleton it could not,
/// since a singleton does not live in the scope.
/// </summary>
public interface IRootCommandConfigurator
{
    void ConfigureRootCommand(RootCommand rootCommand);
}
