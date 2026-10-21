using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: BitWizards.Plugins.PluginModule<BitWizards.Plugins.Test.BrokenPlugin.BrokenModule>]

namespace BitWizards.Plugins.Test.BrokenPlugin;

/// <summary>A plugin whose module fails: the tool should go on without it.</summary>
public sealed class BrokenModule : PluginModule
{
    public override void RegisterServices(IConfiguration configuration, IServiceCollection services)
        => throw new InvalidOperationException("this module does not work");
}
