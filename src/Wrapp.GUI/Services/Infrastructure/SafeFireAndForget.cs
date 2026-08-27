using System;
using System.Threading.Tasks;
using System.Windows;

namespace Wrapp.Services;

/// <summary>
/// Helper for fire-and-forget task invocation that logs and optionally surfaces
/// exceptions instead of letting them vanish. Use instead of bare `_ = SomeAsync()`.
/// </summary>
public static class SafeFireAndForget
{
    /// <summary>
    /// Runs <paramref name="taskFactory"/>. If it throws, logs the exception with
    /// <paramref name="context"/>. If <paramref name="showDialog"/> is true, also
    /// surfaces a FluentDialog exception dialog (marshalled to the UI thread).
    /// OperationCanceledException is logged at Info level only (normal cancellation).
    /// </summary>
    public static async void Run(Func<Task> taskFactory, string context, bool showDialog = false)
    {
        try
        {
            await taskFactory().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info($"[{context}] cancelled");
        }
        catch (Exception ex)
        {
            AppLogger.Exception($"[{context}] fire-and-forget exception", ex);
            if (showDialog)
            {
                try
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher is not null)
                    {
                        await dispatcher.InvokeAsync(
                            async () => await FluentDialog.ShowExceptionAsync(context, ex));
                    }
                }
                catch
                {
                    // Dialog itself failed — already logged the original.
                }
            }
        }
    }
}
