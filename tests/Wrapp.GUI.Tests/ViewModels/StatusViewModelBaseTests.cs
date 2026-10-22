using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Tests.ViewModels;

/// <summary>
/// Covers <see cref="StatusViewModelBase"/>: the IsBusy / StatusText /
/// StatusIsError surface and <c>RunBusyAsync</c> command-handler shell.
/// Uses the <see cref="AppLoggerInstanceTests.CapturingAppLogger"/> pattern
/// (inlined here) to verify exception logging without touching disk.
/// </summary>
public class StatusViewModelBaseTests
{
    // Concrete subclass of the abstract base so we can instantiate it.
    private sealed class TestVm : StatusViewModelBase
    {
        public TestVm(IAppLogger? log = null) : base(log) { }
        public Task RunAsync(Func<Task> work, string ctx = "")
            => RunBusyAsync(work, ctx);
        public Task<T?> RunAsync<T>(Func<Task<T>> work, string ctx = "")
            => RunBusyAsync(work, ctx);
    }

    private sealed class CapturingAppLogger : IAppLogger
    {
        public List<(string Level, string Message)> Entries { get; } = new();
        public void Info(string message)  => Entries.Add(("INFO",  message));
        public void Warn(string message)  => Entries.Add(("WARN",  message));
        public void Error(string message) => Entries.Add(("ERROR", message));
        public void Exception(string context, Exception ex)
            => Entries.Add(("ERROR", $"{context}: {ex.Message}"));
    }

    // ── IsBusy / StatusText / StatusIsError surface ──────────────────

    [Fact]
    public void DefaultState_AllClean()
    {
        var vm = new TestVm();
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.False(vm.StatusIsError);
    }

    // ── RunBusyAsync happy path ──────────────────────────────────────

    [Fact]
    public async Task RunBusyAsync_Success_ClearsBusyAndLeavesStatusEmpty()
    {
        var vm = new TestVm();
        await vm.RunAsync(() => Task.CompletedTask);

        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task RunBusyAsync_Success_IsBusyRaisedDuringRun()
    {
        var vm = new TestVm();
        var sawBusy = false;
        vm.PropertyChanged += (_, e) =>
        {
            // Capture IsBusy=true at the moment it flips on; the TCS in the
            // work func gates when finally sets it back to false.
            if (e.PropertyName == nameof(StatusViewModelBase.IsBusy) && vm.IsBusy)
                sawBusy = true;
        };

        await vm.RunAsync(() => Task.CompletedTask);

        Assert.True(sawBusy, "IsBusy should flip true at least once during run.");
        Assert.False(vm.IsBusy);
    }

    // ── RunBusyAsync exception path ──────────────────────────────────

    [Fact]
    public async Task RunBusyAsync_Exception_SetsErrorStateAndLogs()
    {
        var log = new CapturingAppLogger();
        var vm  = new TestVm(log);

        await vm.RunAsync(() => throw new InvalidOperationException("boom"), "UnitTestCtx");

        Assert.False(vm.IsBusy);
        Assert.Equal("boom", vm.StatusText);
        Assert.True(vm.StatusIsError);
        Assert.Contains(log.Entries, e => e.Level == "WARN" && e.Message.Contains("UnitTestCtx") && e.Message.Contains("boom"));
    }

    [Fact]
    public async Task RunBusyAsync_Exception_WithoutContext_DefaultsToTypeName()
    {
        var log = new CapturingAppLogger();
        var vm  = new TestVm(log);

        await vm.RunAsync(() => throw new InvalidOperationException("oops"));

        Assert.Contains(log.Entries, e => e.Message.StartsWith("TestVm: "));
    }

    // ── Cancellation path ────────────────────────────────────────────

    [Fact]
    public async Task RunBusyAsync_Cancelled_SetsCancelledStatusAndClearsBusy_NoError()
    {
        var log = new CapturingAppLogger();
        var vm  = new TestVm(log);

        await vm.RunAsync(() => Task.FromException(new OperationCanceledException()));

        Assert.False(vm.IsBusy);
        Assert.Equal("Cancelled.", vm.StatusText);
        Assert.False(vm.StatusIsError);
        // Cancellation is normal flow, not logged as a warning.
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Cancelled"));
    }

    // ── Generic RunBusyAsync<T> ──────────────────────────────────────

    [Fact]
    public async Task RunBusyAsyncT_Success_ReturnsWork()
    {
        var vm = new TestVm();
        var result = await vm.RunAsync(() => Task.FromResult(42));
        Assert.Equal(42, result);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task RunBusyAsyncT_Exception_ReturnsDefault()
    {
        var vm = new TestVm(new CapturingAppLogger());
        var result = await vm.RunAsync<int>(() => throw new InvalidOperationException());
        Assert.Equal(0, result);
        Assert.True(vm.StatusIsError);
    }

    // ── OnIsBusyChangedInternal hook fires ───────────────────────────

    [Fact]
    public async Task OnIsBusyChangedInternal_IsInvokedOnEveryTransition()
    {
        var transitions = new List<(bool Old, bool New)>();
        var vm = new VmHook(transitions);

        await vm.RunAsync(() => Task.CompletedTask);

        Assert.Equal(2, transitions.Count);
        Assert.Equal((false, true),  transitions[0]);  // start of RunBusyAsync
        Assert.Equal((true,  false), transitions[1]);  // finally clears
    }

    private sealed class VmHook : StatusViewModelBase
    {
        private readonly List<(bool, bool)> _t;
        public VmHook(List<(bool, bool)> transitions) : base(null) { _t = transitions; }
        protected override void OnIsBusyChangedInternal(bool oldValue, bool newValue)
            => _t.Add((oldValue, newValue));
        public Task RunAsync(Func<Task> w) => RunBusyAsync(w);
    }
}
