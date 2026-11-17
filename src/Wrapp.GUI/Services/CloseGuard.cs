namespace Wrapp.Services;

/// <summary>Why a close is being attempted - selects the context line shown
/// above the standard prompts. Same gates, same choices, every reason.</summary>
public enum CloseReason
{
    /// <summary>The user closed the window (X, Alt-F4, system menu).</summary>
    UserClose,
    /// <summary>This window is closing to hand off to an update install.</summary>
    UpdateHandoff,
    /// <summary>Another Wrapp instance asked this one to close (its update
    /// is waiting on us). Requests only - Cancel works here like anywhere.</summary>
    SiblingCloseRequest,
}

/// <summary>
/// THE close pipeline - the
/// single method every close entry point runs. Replaces the four-barrier
/// re-entrant `Closing` chain in MainWindow (jobs / transfer / bundle /
/// settings, each re-calling <c>Close()</c>), the twice-implemented settings
/// gate, and the mandatory/ordinary prompt fork. There is no mandatory
/// variant anymore: updates are never enforced mid-session,
/// so staying open - Cancel - is always a legal outcome.
///
/// <para>Order of gates, each shown at most once per attempt, each able to
/// abandon the close: active background jobs (confirm cancel-all), transfer
/// in progress (never abandoned - close refused), then one Save / Don't save
/// / Cancel prompt per dirty scope. A save that leaves its scope dirty
/// (validation refused it - missing Company/App/Version) keeps the window
/// open: closing would throw away exactly the work the user asked to keep.</para>
///
/// <para>UI is injected via <see cref="IInteraction"/> so the decision table
/// is unit-testable without WPF.</para>
/// </summary>
public sealed class CloseGuard
{
    /// <summary>The three answers of the standard save prompt.</summary>
    public enum SaveChoice { Save, Discard, Cancel }

    /// <summary>UI surface - MainWindow implements this over FluentDialog.</summary>
    public interface IInteraction
    {
        /// <summary>"N operations running - cancel all and close?" True = proceed.</summary>
        Task<bool> ConfirmCancelJobsAsync(int activeCount, string context);
        /// <summary>Transfers are never abandoned; tell the user and refuse the close.</summary>
        Task NotifyTransferInProgressAsync();
        /// <summary>The standard Save / Don't save / Cancel prompt.</summary>
        Task<SaveChoice> AskSaveAsync(string title, string message);
    }

    /// <summary>Background-job control handles (MainViewModel.JobTracker).</summary>
    public sealed record Jobs(
        Func<bool> HasActive,
        Func<int> ActiveCount,
        Action MarkShuttingDown,
        Action RevertShutdown,
        Func<TimeSpan, Task> CancelAllAndWaitAsync);

    /// <summary>One dirty-state scope (bundle, settings, …) in prompt order.</summary>
    public sealed record Scope(
        string Id,
        string Title,
        string Prompt,
        Func<bool> IsDirty,
        Func<Task> SaveAsync);

    /// <summary>
    /// <see cref="Proceed"/>: close may go ahead. <see cref="SavedScopeIds"/>:
    /// which scopes were saved during THIS attempt (the temp-workspace cleanup
    /// keeps a just-saved draft; a clean or discarded one is deleted).
    /// </summary>
    public readonly record struct Outcome(bool Proceed, IReadOnlyList<string> SavedScopeIds)
    {
        public static Outcome Abandoned => new(false, Array.Empty<string>());
    }

    private static readonly TimeSpan JobCancelWait = TimeSpan.FromSeconds(10);

    private readonly IInteraction _ui;
    private readonly Jobs _jobs;
    private readonly Func<bool> _isTransferring;
    private readonly IReadOnlyList<Scope> _scopes;

    public CloseGuard(IInteraction ui, Jobs jobs, Func<bool> isTransferring, IReadOnlyList<Scope> scopes)
    {
        _ui = ui;
        _jobs = jobs;
        _isTransferring = isTransferring;
        _scopes = scopes;
    }

    /// <summary>Context line prepended to every prompt for non-user closes.</summary>
    internal static string ContextLine(CloseReason reason) => reason switch
    {
        CloseReason.UpdateHandoff       => "An update is waiting to install.\n\n",
        CloseReason.SiblingCloseRequest => "An update in another Wrapp window is waiting on this window.\n\n",
        _                               => string.Empty,
    };

    /// <summary>Runs every gate in order. True = the window may close.</summary>
    public async Task<Outcome> RunAsync(CloseReason reason)
    {
        var context = ContextLine(reason);

        // 1. Active background jobs - confirm cancel-all or abandon the close.
        if (_jobs.HasActive())
        {
            _jobs.MarkShuttingDown();   // red bar + reverse animation while the dialog is up
            var cancelAll = await _ui.ConfirmCancelJobsAsync(_jobs.ActiveCount(), context);
            if (!cancelAll)
            {
                _jobs.RevertShutdown();
                return Outcome.Abandoned;
            }
            await _jobs.CancelAllAndWaitAsync(JobCancelWait);
        }

        // 2. Transfer in progress - data safety: never abandoned, close refused.
        if (_isTransferring())
        {
            await _ui.NotifyTransferInProgressAsync();
            return Outcome.Abandoned;
        }

        // 3. Dirty scopes, in declared order, one standard prompt each.
        var saved = new List<string>();
        foreach (var scope in _scopes)
        {
            if (!scope.IsDirty()) continue;

            switch (await _ui.AskSaveAsync(scope.Title, context + scope.Prompt))
            {
                case SaveChoice.Save:
                    await scope.SaveAsync();
                    if (scope.IsDirty())
                        return Outcome.Abandoned;   // validation refused the save; the view-model said why
                    saved.Add(scope.Id);
                    break;

                case SaveChoice.Discard:
                    break;

                default:
                    return Outcome.Abandoned;       // Cancel - always available, every reason
            }
        }

        return new Outcome(true, saved);
    }
}
