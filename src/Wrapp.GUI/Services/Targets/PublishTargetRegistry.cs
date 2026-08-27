using Wrapp.Models;

namespace Wrapp.Services.Targets;

/// <summary>
/// Resolves an <see cref="IPublishTarget"/> by its <see cref="AppPlatform"/>
/// kind. The single extension point for adding a target: register another
/// <see cref="IPublishTarget"/> here (in <c>CompositionRoot</c>) and the
/// single-surface views (Inventory, Run) can dispatch and gate features
/// against it without new branching.
/// </summary>
public sealed class PublishTargetRegistry
{
    private readonly Dictionary<AppPlatform, IPublishTarget> _byKind;

    public PublishTargetRegistry(IEnumerable<IPublishTarget> targets)
    {
        _byKind = targets.ToDictionary(t => t.Kind);
    }

    /// <summary>All registered targets, in registration order.</summary>
    public IReadOnlyList<IPublishTarget> All => _byKind.Values.ToList();

    /// <summary>
    /// The target for <paramref name="kind"/>. Throws if none is registered —
    /// an unregistered kind is a composition-root bug, not a recoverable state.
    /// </summary>
    public IPublishTarget Get(AppPlatform kind)
        => _byKind.TryGetValue(kind, out var t)
            ? t
            : throw new InvalidOperationException($"No publish target registered for {kind}.");

    /// <summary>Non-throwing lookup.</summary>
    public bool TryGet(AppPlatform kind, out IPublishTarget? target)
    {
        var found = _byKind.TryGetValue(kind, out var t);
        target = t;
        return found;
    }
}
