using Wrapp.Models;

namespace Wrapp.Services.Targets;

/// <summary>
/// A destination wrapp can inventory and publish apps to (Intune or SCCM).
/// Unifies the two parallel stacks behind one contract so single-surface views
/// (Inventory, Run) can dispatch and gate features by <see cref="Capabilities"/>
/// instead of branching on <see cref="AppPlatform"/> / the <c>Target</c> string.
///
/// <para>The concrete targets are thin wrappers over the existing
/// <see cref="AppInventoryService"/> methods — this abstraction changes how
/// call sites <em>select</em> behavior, not the behavior itself. Adding a
/// third target means implementing this interface and registering it; the
/// single-surface views light up the right controls from the capability flags.</para>
/// </summary>
public interface IPublishTarget
{
    /// <summary>Canonical discriminator (reuses the existing serialized enum).</summary>
    AppPlatform Kind { get; }

    /// <summary>User-facing label ("Intune" / "SCCM").</summary>
    string DisplayName { get; }

    /// <summary>What this target supports; drives UI gating and optional dispatch.</summary>
    TargetCapabilities Capabilities { get; }

    /// <summary>
    /// Enumerates apps in the given environment (Intune tenant id / SCCM site
    /// code). Returns the target-specific summary objects
    /// (<c>IntuneAppSummary</c> / <c>SCCMAppSummary</c>) boxed as
    /// <see cref="object"/>, matching how <c>InventoryViewModel</c> already
    /// holds a mixed list and pattern-matches.
    /// </summary>
    Task<IReadOnlyList<object>> GetAppsAsync(string environmentKey, bool forceRefresh = false);

    /// <summary>
    /// Loads the full detail for one app. <paramref name="appIdentifier"/> is
    /// the Graph app id for Intune, the application name for SCCM.
    /// </summary>
    Task<AppInventoryDetail?> GetAppDetailAsync(string environmentKey, string appIdentifier);
}

/// <summary>Capability helpers for <see cref="IPublishTarget"/>.</summary>
public static class PublishTargetExtensions
{
    /// <summary>True if every flag in <paramref name="capability"/> is supported.
    /// An extension (not an interface method) so it's callable on both the
    /// interface and the concrete target types.</summary>
    public static bool Supports(this IPublishTarget target, TargetCapabilities capability)
        => (target.Capabilities & capability) == capability;
}
