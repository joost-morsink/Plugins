using Microsoft.Extensions.DependencyInjection;

namespace BitWizards.Plugins;

public interface IContext
{
    public static IContext<T> Create<T>(T context) => IContext<T>.Create(context);
}

/// <summary>
/// Surfaces ambient data of the invocation (such as the current <c>ParseResult</c> or <c>Command</c>) to services
/// that the container constructed. The data is there for the code that runs in <c>RunWithContext</c>, also across awaits,
/// and only for that code (another thread, or another task, does not see it).
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
    private sealed record Current(T? Value);

    private readonly AsyncLocal<Current?> current = new();

    public T? Context => current.Value is { } running ? running.Value : defaultValue;
    public bool HasContext => Context is not null;

    public async Task<R> RunWithContextAsync<R>(T context, Func<Task<R>> func)
    {
        var before = current.Value;
        current.Value = new Current(context);
        try
        {
            return await func();
        }
        finally
        {
            current.Value = before;
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
        var before = current.Value;
        current.Value = new Current(context);
        try
        {
            return func();
        }
        finally
        {
            current.Value = before;
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
    /// <summary>Makes an <see cref="IContext{T}"/> available, without a context until one is run with.</summary>
    public static IServiceCollection AddContext<T>(this IServiceCollection services)
        => services.AddSingleton<IContext<T>>(_ => new ContextHolder<T>());

    /// <summary>Makes an <see cref="IContext{T}"/> available, with a context for the code that does not run with another.</summary>
    public static IServiceCollection AddContext<T>(this IServiceCollection services, T defaultValue)
        => services.AddSingleton<IContext<T>>(_ => new ContextHolder<T>(defaultValue));
}
