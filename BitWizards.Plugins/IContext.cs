using Microsoft.Extensions.DependencyInjection;

namespace BitWizards.Plugins;

/// <summary>
/// Surfaces ambient data of the invocation (such as the current <c>ParseResult</c> or <c>Command</c>) to services
/// that the container constructed. The data is there for the code that runs in <see cref="RunWithContext{R}"/>, also across awaits.
/// </summary>
public interface IContext<T>
{
    T? Context { get; }
    bool HasContext { get; }
    R RunWithContext<R>(T context, Func<R> func);
}

public sealed class ContextHolder<T> : IContext<T>
{
    private readonly AsyncLocal<(T? Context, bool Has)> current = new();

    public T? Context => current.Value.Context;
    public bool HasContext => current.Value.Has;

    public R RunWithContext<R>(T context, Func<R> func)
    {
        var before = current.Value;
        current.Value = (context, true);
        try
        {
            return func();
        }
        finally
        {
            current.Value = before;
        }
    }
}

public static class ContextServiceCollectionExtensions
{
    /// <summary>Makes an <see cref="IContext{T}"/> available.</summary>
    public static IServiceCollection AddContext<T>(this IServiceCollection services)
        => services.AddSingleton<IContext<T>, ContextHolder<T>>();
}
