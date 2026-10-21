using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BitWizards.Plugins;

/// <summary>
/// The entry point of a plugin. A module can add configuration sources and register services
/// (including <see cref="IRootCommandConfigurator"/>s that contribute commands).
/// </summary>
public interface IPluginModule
{
    /// <summary>Adds configuration sources. Runs before the configuration is built, so it can not read the configuration.</summary>
    void Configure(IConfigurationBuilder configurationBuilder);

    /// <summary>Registers services, after those of the tool itself: a plugin can replace what the tool registered.</summary>
    void RegisterServices(IConfiguration configuration, IServiceCollection services);
}

/// <summary>Convenience base class for a module: override only what you need.</summary>
public abstract class PluginModule : IPluginModule
{
    public virtual void Configure(IConfigurationBuilder configurationBuilder)
    {
    }

    public virtual void RegisterServices(IConfiguration configuration, IServiceCollection services)
    {
    }
}

/// <summary>
/// A module that stands for a module of another contract, as the ones that <see cref="PluginHostOptions.ModuleDiscovery"/> wraps.
/// <c>plugins list</c> and the warnings name the module that is wrapped, not the wrapper that all of them share.
/// </summary>
public interface IPluginModuleWrapper : IPluginModule
{
    /// <summary>The module that is wrapped.</summary>
    object Module { get; }
}

/// <summary>
/// Discovery marker, applied to the assembly of a plugin: <c>[assembly: PluginModule(typeof(MyModule))]</c>.
/// The module needs a public constructor without parameters.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class PluginModuleAttribute(Type moduleType) : Attribute
{
    public Type ModuleType { get; } = moduleType;

    public IPluginModule CreateModule()
        => typeof(IPluginModule).IsAssignableFrom(ModuleType)
            ? (IPluginModule)Activator.CreateInstance(ModuleType)!
            : throw new InvalidOperationException($"{ModuleType.FullName} is not an {nameof(IPluginModule)}.");
}

/// <summary>Generic form, which the compiler checks: <c>[assembly: PluginModule&lt;MyModule&gt;]</c>.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class PluginModuleAttribute<T>() : PluginModuleAttribute(typeof(T)) where T : IPluginModule, new();
