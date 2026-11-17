using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.Models;

/// <summary>One labeled fact on a job's detail card ("Apps: 142", "From: \\share\…").</summary>
public sealed partial class JobFact : ObservableObject
{
    public string Label { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPathValue))]
    private string _value = string.Empty;

    /// <summary>True when the value looks like a filesystem path (rooted or
    /// UNC, single line) - the details card then offers the standard
    /// open-in-Explorer + copy-path icon buttons beside it.</summary>
    public bool IsPathValue
    {
        get
        {
            var v = Value;
            if (string.IsNullOrWhiteSpace(v) || v.Contains('\n')) return false;
            try
            {
                return v.StartsWith(@"\\", StringComparison.Ordinal)
                    || (v.Length > 3 && System.IO.Path.IsPathRooted(v) && v.Contains('\\'));
            }
            catch { return false; }
        }
    }
}

/// <summary>
/// General-purpose structured detail for a background job - the counterpart
/// to the run's <c>PackagingRunContext</c> tree for every OTHER kind of job.
/// Call sites attach facts as they learn them (counts, paths, statistics,
/// result summaries) via <see cref="JobHandle.SetDetail"/>, and error
/// payloads (code + raw response body) via <see cref="JobHandle.SetError"/>;
/// the jobs pop-up renders it all in the card's Details drop-down
/// (<c>JobDetailsRenderer</c>).
///
/// <para>Facts must be ADDED on the dispatcher (ObservableCollection);
/// updating an existing fact's value is safe from any thread (plain INPC).
/// The upsert semantics make live counters cheap: set the same label again
/// and only the value changes.</para>
/// </summary>
public sealed partial class JobDetails : ObservableObject
{
    public ObservableCollection<JobFact> Facts { get; } = new();

    /// <summary>Short machine-ish error identifier (HTTP status, exception type).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorCode;

    /// <summary>Raw error payload (response body, exception message) - shown
    /// verbatim in a scrollable monospace box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorBody;

    public bool HasError => !string.IsNullOrEmpty(ErrorCode) || !string.IsNullOrEmpty(ErrorBody);

    /// <summary>Adds or updates a fact by label (upsert; ordered by first add).
    /// Safe from any thread: the value-update path is plain INPC; the ADD path
    /// (ObservableCollection mutation) marshals to the dispatcher.
    /// Back-ported from Unwrapped, where engine continuations run on
    /// ConfigureAwait(false) threads and the caller rule "adds belong on the
    /// dispatcher" guaranteed a NotSupportedException the moment the jobs
    /// popup had the card open.</summary>
    public void Set(string label, string value)
    {
        foreach (var fact in Facts)
        {
            if (string.Equals(fact.Label, label, StringComparison.Ordinal))
            {
                fact.Value = value;
                return;
            }
        }
        DispatcherHelper.RunOnUi(() =>
        {
            // Re-check under the dispatcher: a racing add for the same label
            // may have landed while this call was queued.
            foreach (var fact in Facts)
            {
                if (string.Equals(fact.Label, label, StringComparison.Ordinal))
                {
                    fact.Value = value;
                    return;
                }
            }
            Facts.Add(new JobFact { Label = label, Value = value });
        });
    }
}

/// <summary>
/// Marshals UI-bound mutations from engine threads. Inline when already on
/// the dispatcher or when no WPF Application exists (unit tests, headless),
/// so tests exercise the same code path. Back-ported from Unwrapped.
/// </summary>
internal static class DispatcherHelper
{
    public static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public static T RunOnUi<T>(Func<T> func)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) return func();
        return dispatcher.Invoke(func);
    }
}
