using System.ComponentModel;

namespace Wrapp.ViewModels;

/// <summary>
/// <para>
/// Declarative helper for the "child VM property changed → raise one or more
/// notifications on a parent VM" pattern. Replaces hand-rolled wirings like:
/// </para>
/// <code>
/// vm.PropertyChanged += (_, e) =>
/// {
///     if (e.PropertyName == nameof(IntuneViewModel.TotalErrorCount))
///         OnPropertyChanged(nameof(IntuneErrorCount));
/// };
/// </code>
/// <para>with:</para>
/// <code>
/// PropertyRelay.Wire(vm, OnPropertyChanged,
///     PropertyRelay.When(nameof(IntuneViewModel.TotalErrorCount),
///                        nameof(IntuneErrorCount)));
/// </code>
/// <para>
/// Supports 1:N fan-out (one source property triggers multiple target
/// notifications). The parent passes its own <c>OnPropertyChanged</c> as an
/// <c>Action&lt;string&gt;</c> because the method is <c>protected</c> on
/// <c>ObservableObject</c>, so an external helper can't call it directly.
/// Delegate-bound to the caller instance, it reaches the generator-produced
/// event with no reflection.
/// </para>
/// </summary>
public static class PropertyRelay
{
    /// <summary>
    /// A single source-property-name → target-property-name(s) rule.
    /// Use <see cref="When"/> to construct readable call sites.
    /// </summary>
    public readonly record struct Relay(string Source, IReadOnlyList<string> Targets);

    /// <summary>
    /// Builds a <see cref="Relay"/> for the given source with one or more
    /// target property names. Zero targets is allowed (effectively a no-op
    /// relay; useful during refactor while target names are being sorted out).
    /// </summary>
    public static Relay When(string source, params string[] targets) => new(source, targets);

    /// <summary>
    /// Subscribes to <paramref name="source"/>'s <c>PropertyChanged</c> event
    /// and, whenever the raised property name matches any relay's source,
    /// invokes <paramref name="raiseOnTarget"/> once per configured target.
    /// </summary>
    /// <remarks>
    /// No unsubscribe helper today — parent VMs currently outlive their
    /// child VMs, so leaks are not a concern. If that changes, return an
    /// IDisposable that removes the handler.
    /// </remarks>
    public static void Wire(
        INotifyPropertyChanged source,
        Action<string> raiseOnTarget,
        params Relay[] relays)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (raiseOnTarget is null) throw new ArgumentNullException(nameof(raiseOnTarget));
        if (relays is null || relays.Length == 0) return;

        source.PropertyChanged += (_, e) =>
        {
            var name = e.PropertyName;
            if (string.IsNullOrEmpty(name)) return;
            foreach (var relay in relays)
            {
                if (relay.Source != name) continue;
                var targets = relay.Targets;
                for (int i = 0; i < targets.Count; i++)
                    raiseOnTarget(targets[i]);
            }
        };
    }
}
