using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Services;

namespace Wrapp.ViewModels;

/// <summary>
/// <para>
/// Base class for view-models that expose a busy / status-text / error-flag
/// surface. Standardises the three properties several VMs previously re-
/// declared independently, and owns <see cref="RunBusyAsync"/> - a command-
/// handler shell that wraps the recurring try / catch / finally boilerplate:
/// set <see cref="IsBusy"/>, scrub status, execute work, surface any
/// exception, always clear busy.
/// </para>
/// <para>
/// Semantic-naming VMs (Account&apos;s <c>IsAuthenticating</c>, Inventory&apos;s
/// <c>IsLoading</c>, Run&apos;s <c>IsRunning</c>) stay on their own property
/// names intentionally - the domain-specific label is documentation we
/// don&apos;t want to lose. Only VMs whose properties were already called
/// IsBusy / StatusText / StatusIsError inherit this base today. The pattern
/// stays available for the refactor that splits those VMs in a later phase.
/// </para>
/// </summary>
public abstract partial class StatusViewModelBase : ObservableObject
{
    /// <summary>
    /// Logger used by <see cref="RunBusyAsync"/> when a command throws.
    /// Injectable via ctor so tests can capture log output with a fake.
    /// Defaults to the shared <see cref="AppLogger.Instance"/>.
    /// </summary>
    private readonly IAppLogger _log;

    protected StatusViewModelBase(IAppLogger? log = null)
    {
        _log = log ?? AppLogger.Instance;
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _statusIsError;

    // MVVM Toolkit's [ObservableProperty] source generator emits partial
    // OnXxxChanged methods callable ONLY from the same partial class. We
    // forward them to protected virtual hooks so subclasses can react (e.g.
    // NotifyCanExecuteChanged on IsBusy) without us having to replicate the
    // [NotifyCanExecuteChangedFor] attribute mechanism in subclass code.
    partial void OnIsBusyChanged(bool oldValue, bool newValue)
        => OnIsBusyChangedInternal(oldValue, newValue);
    partial void OnStatusTextChanged(string? oldValue, string newValue)
        => OnStatusTextChangedInternal(oldValue ?? string.Empty, newValue);
    partial void OnStatusIsErrorChanged(bool oldValue, bool newValue)
        => OnStatusIsErrorChangedInternal(oldValue, newValue);

    /// <summary>Override to react to <see cref="IsBusy"/> changes (e.g. refresh command CanExecute).</summary>
    protected virtual void OnIsBusyChangedInternal(bool oldValue, bool newValue) { }
    /// <summary>Override to react to <see cref="StatusText"/> changes.</summary>
    protected virtual void OnStatusTextChangedInternal(string oldValue, string newValue) { }
    /// <summary>Override to react to <see cref="StatusIsError"/> changes.</summary>
    protected virtual void OnStatusIsErrorChangedInternal(bool oldValue, bool newValue) { }

    /// <summary>
    /// Standard command-handler shell for async work. Sets <see cref="IsBusy"/>
    /// around the call, clears <see cref="StatusText"/> / <see cref="StatusIsError"/>
    /// on entry, logs + surfaces any exception, and guarantees IsBusy is cleared
    /// even if the work throws. Cancellation is treated distinctly (non-error
    /// status message, not logged as an error).
    /// </summary>
    /// <param name="work">The async operation to run.</param>
    /// <param name="errorContext">
    /// Label prepended to the log line on exception. Defaults to the concrete
    /// VM type name when empty - sensible fallback for ad-hoc call sites.
    /// </param>
    protected async Task RunBusyAsync(Func<Task> work, string errorContext = "")
    {
        StatusText = string.Empty;
        StatusIsError = false;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
            var ctx = string.IsNullOrEmpty(errorContext) ? GetType().Name : errorContext;
            _log.Warn($"{ctx}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Variant of <see cref="RunBusyAsync(Func{Task},string)"/> for work that
    /// returns a value. Returns <c>default(T)</c> on cancellation or
    /// exception; callers that need to distinguish success from failure
    /// should check <see cref="StatusIsError"/> after awaiting.
    /// </summary>
    protected async Task<T?> RunBusyAsync<T>(Func<Task<T>> work, string errorContext = "")
    {
        StatusText = string.Empty;
        StatusIsError = false;
        IsBusy = true;
        try
        {
            return await work();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            return default;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
            var ctx = string.IsNullOrEmpty(errorContext) ? GetType().Name : errorContext;
            _log.Warn($"{ctx}: {ex.Message}");
            return default;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
