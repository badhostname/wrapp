using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase C (update-flow-and-token-polling-plan): the one close pipeline.
/// The decision table under test encodes hard-won behavior ported from the
/// old four-barrier MainWindow chain: jobs confirm-cancel with red-bar
/// revert, transfers never abandoned, one prompt per dirty scope in order,
/// validation-refused saves keep the window open, Cancel always available,
/// and the temp-workspace rule (a draft saved during THIS close survives).
/// </summary>
public class CloseGuardTests
{
    // ------------------------------------------------------------------
    // Scripted fakes
    // ------------------------------------------------------------------

    private sealed class FakeUi : CloseGuard.IInteraction
    {
        public bool ConfirmCancelJobsAnswer = true;
        public Queue<CloseGuard.SaveChoice> SaveAnswers = new();
        public List<string> PromptTitles = new();
        public List<string> PromptMessages = new();
        public int JobsPromptCount;
        public int TransferNoticeCount;
        public string? JobsContext;

        public Task<bool> ConfirmCancelJobsAsync(int activeCount, string context)
        {
            JobsPromptCount++;
            JobsContext = context;
            return Task.FromResult(ConfirmCancelJobsAnswer);
        }

        public Task NotifyTransferInProgressAsync()
        {
            TransferNoticeCount++;
            return Task.CompletedTask;
        }

        public Task<CloseGuard.SaveChoice> AskSaveAsync(string title, string message)
        {
            PromptTitles.Add(title);
            PromptMessages.Add(message);
            return Task.FromResult(SaveAnswers.Count > 0 ? SaveAnswers.Dequeue() : CloseGuard.SaveChoice.Cancel);
        }
    }

    private sealed class Harness
    {
        public FakeUi Ui = new();
        public bool JobsActive;
        public bool Transferring;
        public bool BundleDirty;
        public bool SettingsDirty;
        public bool BundleSaveClears = true;    // false = validation refuses the save
        public bool SettingsSaveClears = true;
        public int  JobsCancelled;
        public bool ShutdownMarked, ShutdownReverted;

        public CloseGuard Build() => new(
            Ui,
            new CloseGuard.Jobs(
                HasActive:             () => JobsActive,
                ActiveCount:           () => JobsActive ? 2 : 0,
                MarkShuttingDown:      () => ShutdownMarked = true,
                RevertShutdown:        () => ShutdownReverted = true,
                CancelAllAndWaitAsync: _ => { JobsCancelled++; JobsActive = false; return Task.CompletedTask; }),
            isTransferring: () => Transferring,
            scopes: new[]
            {
                new CloseGuard.Scope("bundle", "Save Progress", "Save the bundle before closing?",
                    () => BundleDirty,
                    () => { if (BundleSaveClears) BundleDirty = false; return Task.CompletedTask; }),
                new CloseGuard.Scope("settings", "Save Settings", "Save them before closing?",
                    () => SettingsDirty,
                    () => { if (SettingsSaveClears) SettingsDirty = false; return Task.CompletedTask; }),
            });
    }

    // ------------------------------------------------------------------
    // Clean close
    // ------------------------------------------------------------------

    [Fact]
    public async Task Clean_ProceedsWithNoPrompts()
    {
        var h = new Harness();
        var outcome = await h.Build().RunAsync(CloseReason.UserClose);

        Assert.True(outcome.Proceed);
        Assert.Empty(outcome.SavedScopeIds);
        Assert.Equal(0, h.Ui.JobsPromptCount);
        Assert.Empty(h.Ui.PromptTitles);
    }

    // ------------------------------------------------------------------
    // Jobs gate
    // ------------------------------------------------------------------

    [Fact]
    public async Task ActiveJobs_Declined_RevertsAndStaysOpen()
    {
        var h = new Harness { JobsActive = true };
        h.Ui.ConfirmCancelJobsAnswer = false;

        var outcome = await h.Build().RunAsync(CloseReason.UserClose);

        Assert.False(outcome.Proceed);
        Assert.True(h.ShutdownMarked);      // red bar shown while the dialog was up
        Assert.True(h.ShutdownReverted);    // and reverted on decline
        Assert.Equal(0, h.JobsCancelled);
    }

    [Fact]
    public async Task ActiveJobs_Confirmed_CancelsThenContinuesToScopes()
    {
        var h = new Harness { JobsActive = true, BundleDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Discard);

        var outcome = await h.Build().RunAsync(CloseReason.UserClose);

        Assert.True(outcome.Proceed);
        Assert.Equal(1, h.JobsCancelled);
        Assert.Equal(new[] { "Save Progress" }, h.Ui.PromptTitles);
    }

    // ------------------------------------------------------------------
    // Transfer gate — never abandoned
    // ------------------------------------------------------------------

    [Fact]
    public async Task Transfer_RefusesClose_EvenForUpdateHandoff()
    {
        var h = new Harness { Transferring = true };
        var outcome = await h.Build().RunAsync(CloseReason.UpdateHandoff);

        Assert.False(outcome.Proceed);
        Assert.Equal(1, h.Ui.TransferNoticeCount);
        Assert.Empty(h.Ui.PromptTitles);    // never reached the scopes
    }

    // ------------------------------------------------------------------
    // Dirty scopes
    // ------------------------------------------------------------------

    [Fact]
    public async Task DirtyBundle_Cancel_StaysOpen()
    {
        var h = new Harness { BundleDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Cancel);

        Assert.False((await h.Build().RunAsync(CloseReason.UserClose)).Proceed);
    }

    [Fact]
    public async Task DirtyBundle_Save_ProceedsAndReportsSaved()
    {
        var h = new Harness { BundleDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Save);

        var outcome = await h.Build().RunAsync(CloseReason.UserClose);

        Assert.True(outcome.Proceed);
        Assert.Contains("bundle", outcome.SavedScopeIds);
    }

    [Fact]
    public async Task SaveRefusedByValidation_StaysOpen()
    {
        // The 0.6.322 data-loss bug, as a contract: Save chosen, validation
        // refuses (missing Company/App/Version), the window must stay open.
        var h = new Harness { BundleDirty = true, BundleSaveClears = false };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Save);

        var outcome = await h.Build().RunAsync(CloseReason.UserClose);

        Assert.False(outcome.Proceed);
    }

    [Fact]
    public async Task BothDirty_PromptsInOrder_MixedAnswers()
    {
        var h = new Harness { BundleDirty = true, SettingsDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Discard);   // bundle
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Save);      // settings

        var outcome = await h.Build().RunAsync(CloseReason.UserClose);

        Assert.True(outcome.Proceed);
        Assert.Equal(new[] { "Save Progress", "Save Settings" }, h.Ui.PromptTitles);
        Assert.Equal(new[] { "settings" }, outcome.SavedScopeIds);
    }

    [Fact]
    public async Task SettingsOnlyDirty_SinglePrompt()
    {
        var h = new Harness { SettingsDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Save);

        var outcome = await h.Build().RunAsync(CloseReason.UserClose);

        Assert.True(outcome.Proceed);
        Assert.Equal(new[] { "Save Settings" }, h.Ui.PromptTitles);
    }

    [Fact]
    public async Task SecondScopeCancel_AbandonsAfterFirstDiscard()
    {
        // The old pipeline honored a settings-gate cancel even after the
        // bundle gate passed; the unified walk must too.
        var h = new Harness { BundleDirty = true, SettingsDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Discard);
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Cancel);

        Assert.False((await h.Build().RunAsync(CloseReason.UserClose)).Proceed);
    }

    // ------------------------------------------------------------------
    // Context lines per reason
    // ------------------------------------------------------------------

    [Fact]
    public async Task UpdateHandoff_PrependsContextToPrompts()
    {
        var h = new Harness { JobsActive = true, BundleDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Discard);

        await h.Build().RunAsync(CloseReason.UpdateHandoff);

        Assert.StartsWith("An update is waiting to install.", h.Ui.JobsContext);
        Assert.StartsWith("An update is waiting to install.", h.Ui.PromptMessages[0]);
    }

    [Fact]
    public async Task SiblingRequest_PrependsItsOwnContext()
    {
        var h = new Harness { BundleDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Discard);

        await h.Build().RunAsync(CloseReason.SiblingCloseRequest);

        Assert.StartsWith("An update in another Wrapp window", h.Ui.PromptMessages[0]);
    }

    [Fact]
    public async Task UserClose_HasNoContextLine()
    {
        var h = new Harness { BundleDirty = true };
        h.Ui.SaveAnswers.Enqueue(CloseGuard.SaveChoice.Discard);

        await h.Build().RunAsync(CloseReason.UserClose);

        Assert.StartsWith("Save the bundle", h.Ui.PromptMessages[0]);
    }
}
