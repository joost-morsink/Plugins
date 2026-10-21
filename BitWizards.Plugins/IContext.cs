using Microsoft.Extensions.DependencyInjection;

namespace BitWizards.Plugins;

public interface IContext
{
    public static IContext<T> Create<T>(T context) => IContext<T>.Create(context);
}

/// <summary>
/// Surfaces data of the invocation (such as the current <c>ParseResult</c> or <c>Command</c>) to services that the container constructed.
/// The context is that of the instance, so of the scope it is resolved from: it is set for the code that runs in <c>RunWithContext</c> (which says where it
/// starts and ends, and puts back what was there), and is the same for everything that is resolved from that scope. Code that runs at the same time in one scope
/// shares it, so give each its own scope.
/// </summary>
public interface IContext<T>
{
    /// <summary>Whether there is a context: one is being run with, or there is a default value.</summary>
    bool HasContext { get; }

    T? Context { get; }

    Task<R> RunWithContextAsync<R>(T context, Func<Task<R>> func);
    Task RunWithContextAsync(T context, Func<Task> func);
    R RunWithContext<R>(T context, Func<R> func);
    void RunWithContext(T context, Action action);

    public static IContext<T> Create() => new ContextHolder<T>();
    public static IContext<T> Create(T context) => new ContextHolder<T>(context);
}

public sealed class ContextHolder<T>(T? defaultValue = default) : IContext<T>
{
    public T? Context { get; private set; } = defaultValue;
    public bool HasContext => Context is not null;

    public async Task<R> RunWithContextAsync<R>(T context, Func<Task<R>> func)
    {
        var before = Context;
        Context = context;
        try
        {
            return await func();
        }
        finally
        {
            Context = before;
        }
    }

    public async Task RunWithContextAsync(T context, Func<Task> func)
        => await RunWithContextAsync(context, async () =>
        {
            await func();
            return 0;
        });

    public R RunWithContext<R>(T context, Func<R> func)
    {
        var before = Context;
        Context = context;
        try
        {
            return func();
        }
        finally
        {
            Context = before;
        }
    }

    public void RunWithContext(T context, Action action)
        => RunWithContext(context, () =>
        {
            action();
            return 0;
        });
}

public static class ContextServiceCollectionExtensions
{
    /// <summary>Makes an <see cref="IContext{T}"/> available to each scope, without a context until one is run with.</summary>
    public static IServiceCollection AddContext<T>(this IServiceCollection services)
        => services.AddScoped<IContext<T>>(_ => new ContextHolder<T>());

    /// <summary>Makes an <see cref="IContext{T}"/> available to each scope, with a context for the code that does not run with another.</summary>
    public static IServiceCollection AddContext<T>(this IServiceCollection services, T defaultValue)
        => services.AddScoped<IContext<T>>(_ => new ContextHolder<T>(defaultValue));
}
