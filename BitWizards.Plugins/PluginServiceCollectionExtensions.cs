using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BitWizards.Plugins;

public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services of a module that is part of the tool itself, as a module from a plugin would: for the modules that a tool has built in.
    /// (<see cref="IPluginModule.Configure"/> is for the configuration, which is built before; a built-in module adds its sources to the configuration itself.)
    /// </summary>
    public static IServiceCollection AddPluginModule<T>(this IServiceCollection services, IConfiguration configuration) where T : IPluginModule, new()
    {
        new T().RegisterServices(configuration, services);
        return services;
    }
}
