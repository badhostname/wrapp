namespace Wrapp.Services;

/// <summary>
/// Minimal type-keyed service store backing <see cref="CompositionRoot"/> and
/// <c>App.GetService&lt;T&gt;()</c>. Deliberately not a DI container — wrapp's
/// startup builds the object graph by hand (the graph has a constructor cycle
/// between <c>MainViewModel</c> and the package/tenant view-models that a
/// container can't resolve anyway), so this just gives one lookup surface for
/// views and later workstreams to fetch already-constructed singletons.
///
/// <para>Not thread-safe: all registration happens on one thread during
/// <see cref="CompositionRoot.BuildAsync"/>, and reads happen afterward. If
/// registration ever moves off the startup thread, add a lock.</para>
/// </summary>
public sealed class ServiceRegistry
{
    private readonly Dictionary<Type, object> _services = new();

    /// <summary>
    /// Registers <paramref name="instance"/> under the compile-time type
    /// <typeparamref name="T"/> (so <c>Register&lt;IFoo&gt;(impl)</c> keys by
    /// the interface). Replaces any existing registration for that key.
    /// Returns the instance so calls can be chained inline with construction.
    /// </summary>
    public T Register<T>(T instance) where T : class
    {
        _services[typeof(T)] = instance;
        return instance;
    }

    /// <summary>
    /// Resolves the instance registered under <typeparamref name="T"/>.
    /// Throws <see cref="InvalidOperationException"/> if none is registered —
    /// a missing service is a composition-root bug, not a recoverable state.
    /// </summary>
    public T Get<T>() where T : class
        => _services.TryGetValue(typeof(T), out var svc)
            ? (T)svc
            : throw new InvalidOperationException(
                $"No service registered for {typeof(T).Name}. " +
                "Register it in CompositionRoot.BuildAsync.");

    /// <summary>
    /// Non-throwing resolve. Returns false and null when <typeparamref name="T"/>
    /// is not registered — used by <c>App.InventoryService</c> which can be read
    /// before the root is built.
    /// </summary>
    public bool TryGet<T>(out T? instance) where T : class
    {
        if (_services.TryGetValue(typeof(T), out var svc))
        {
            instance = (T)svc;
            return true;
        }
        instance = null;
        return false;
    }

    /// <summary>True if a service is registered under <typeparamref name="T"/>.</summary>
    public bool Contains<T>() where T : class => _services.ContainsKey(typeof(T));
}
