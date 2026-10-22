using System.Windows;

namespace Wrapp.Services;

/// <summary>
/// <para>
/// Factory helpers for <see cref="IProgress{T}"/> instances that marshal
/// each <see cref="IProgress{T}.Report"/> call onto the WPF UI thread before
/// invoking the supplied setter.
/// </para>
/// <para>
/// Replaces the recurring boilerplate that previously appeared in
/// <c>InventoryViewModel.Actions</c> and friends:
/// <code>
/// new Progress&lt;string&gt;(msg =&gt;
///     System.Windows.Application.Current.Dispatcher.Invoke(() =&gt; StatusText = msg))
/// </code>
/// becomes <c>UiProgress.ForStatus(s =&gt; StatusText = s)</c>.
/// </para>
/// <para>
/// Why the explicit <c>Dispatcher.Invoke</c> when <see cref="Progress{T}"/>
/// already captures <c>SynchronizationContext.Current</c>? Because some
/// callers construct their progress reporter from inside a
/// <c>Task.Run</c> continuation, where <c>SynchronizationContext.Current</c>
/// is the threadpool's null context, not the WPF dispatcher. Forcing the
/// marshaling here is belt-and-braces - safe in both UI-thread and
/// background-thread construction contexts.
/// </para>
/// </summary>
public static class UiProgress
{
    /// <summary>
    /// Creates an <see cref="IProgress{String}"/> whose every report is
    /// dispatched to the WPF UI thread before invoking <paramref name="setter"/>.
    /// Typical use is for status-text progress streams from background work:
    /// <code>
    /// var p = UiProgress.ForStatus(s =&gt; StatusText = s);
    /// await someService.DoLongThingAsync(p);
    /// </code>
    /// </summary>
    public static IProgress<string> ForStatus(Action<string> setter)
        => new Progress<string>(msg =>
            Application.Current?.Dispatcher.Invoke(() => setter(msg)));

    /// <summary>
    /// Int-valued sibling of <see cref="ForStatus"/>. Use for percentage /
    /// completion-count progress bars updated from background work:
    /// <code>
    /// var p = UiProgress.ForProgress(n =&gt; ProgressPercent = n);
    /// await someService.DoLongThingAsync(p);
    /// </code>
    /// Marshals onto the UI thread the same way <see cref="ForStatus"/> does.
    /// </summary>
    public static IProgress<int> ForProgress(Action<int> setter)
        => new Progress<int>(value =>
            Application.Current?.Dispatcher.Invoke(() => setter(value)));
}
