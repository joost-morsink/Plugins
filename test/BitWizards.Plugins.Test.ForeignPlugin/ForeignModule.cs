using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: BitWizards.Plugins.Test.ForeignPlugin.ForeignModule(typeof(BitWizards.Plugins.Test.ForeignPlugin.ForeignModule))]

namespace BitWizards.Plugins.Test.ForeignPlugin;

/// <summary>The module contract of a tool of its own, as the plugins of such a tool declare it.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ForeignModuleAttribute(Type moduleType) : Attribute
{
    public Type ModuleType { get; } = moduleType;
}

public sealed class ForeignModule
{
    public void RegisterServices(IConfiguration configuration, IServiceCollection services)
        => services.AddSingleton(new ForeignMarker(configuration["Foreign:Value"] ?? "none"));
}

public sealed record ForeignMarker(string Value);
