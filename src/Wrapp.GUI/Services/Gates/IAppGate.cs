using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>How a pending gate is surfaced to the user.</summary>
public enum GateKind
{
    /// <summary>
    /// Must be resolved before the user can use the app (liability waiver,
    /// mandatory migration). Resolved via a sequential startup modal; declining
    /// shuts the app down.
    /// </summary>
    Blocking,

    /// <summary>
    /// Should be resolved, but the app stays usable (e.g. re-approve a vault URL
    /// you might not use this session). Surfaced via the status-bar "action
    /// needed" indicator; the user resolves on demand.
    /// </summary>
    Advisory,
}

/// <summary>
/// A discrete, resolvable user action that a code / config / version change may
/// require — re-consent, re-approval, a mandatory field, a post-update
/// migration prompt. Each concern implements ONE gate; <see cref="GateService"/>
/// evaluates and surfaces them all uniformly.
/// <para>
/// Deliberately the *only* extension point: a future requirement is one
/// <see cref="IAppGate"/> plus (optionally) one persistence key in
/// <see cref="AppSettings.GateState"/>, with no changes to the framework or the
/// UI. This is intentionally separate from the "dirty" flag, which means
/// "unsaved edits" — an orthogonal concern that cannot express blocking gates,
/// persisted resolutions, or version-triggered requirements.
/// </para>
/// </summary>
public interface IAppGate
{
    /// <summary>Stable identifier; also the persistence key in <see cref="AppSettings.GateState"/>.</summary>
    string Id { get; }

    /// <summary>Blocking vs advisory — see <see cref="GateKind"/>.</summary>
    GateKind Kind { get; }

    /// <summary>Short operator-facing title (shown in the indicator / modal).</summary>
    string Title { get; }

    /// <summary>
    /// True when the action is currently required, evaluated against
    /// <paramref name="settings"/> (and any versions it reads). Must be pure and
    /// side-effect free so it can be called cheaply and unit-tested without UI.
    /// </summary>
    bool IsPending(AppSettings settings);

    /// <summary>
    /// Runs the resolution UX (dialog / wizard / approval) and, on success,
    /// mutates <paramref name="settings"/> so <see cref="IsPending"/> returns
    /// false next time. Returns true when resolved, false when the user
    /// declined / cancelled. The caller (<see cref="GateService"/>) persists
    /// <paramref name="settings"/> — gates never save directly.
    /// </summary>
    Task<bool> ResolveAsync(AppSettings settings);
}
