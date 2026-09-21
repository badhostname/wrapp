using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace Wrapp;

/// <summary>
/// Reusable window behavior helpers.
/// </summary>
public static class WindowHelper
{
    /// <summary>
    /// Prevents a window from closing while the predicate returns true.
    /// Used to block the X button and Alt+F4 during in-progress operations
    /// (e.g. workspace creation, file restore).
    /// </summary>
    public static void PreventCloseWhile(Window window, Func<bool> isBusy)
    {
        window.Closing += (_, e) =>
        {
            if (isBusy())
                e.Cancel = true;
        };
    }

    /// <summary>
    /// Returns the Win32 HWND of the application's main window, or
    /// <see cref="IntPtr.Zero"/> if there is no main window yet (e.g. during
    /// the early splash phase). WAM auth dialogs and other Win32 dialog APIs
    /// take an owner HWND, and the recurring pattern across view-models was:
    /// <code>
    /// var window = Application.Current.MainWindow;
    /// var hwnd = window is null
    ///     ? IntPtr.Zero
    ///     : new System.Windows.Interop.WindowInteropHelper(window).Handle;
    /// </code>
    /// Centralised here so future callers don't have to remember the WPF +
    /// Interop-namespace dance.
    /// </summary>
    public static IntPtr GetMainWindowHwnd()
    {
        var window = Application.Current?.MainWindow;
        return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }

    /// <summary>
    /// Returns the Win32 HWND of a specific window, or <see cref="IntPtr.Zero"/>
    /// before its source is initialized.
    /// </summary>
    public static IntPtr GetWindowHandle(Window window)
        => new WindowInteropHelper(window).Handle;
}
