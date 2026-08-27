using System;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Wrapp.Services;

namespace Wrapp.Helpers;

/// <summary>
/// Keeps the taskbar icon from reverting to the generic window glyph.
/// Windows 11's taskbar snapshots a window's icon early and re-resolves it on
/// DPI/monitor re-evaluation (lock-unlock, restart, RDP). Two documented
/// failure modes hit apps like Wrapp:
/// <list type="bullet">
/// <item><description>dotnet/wpf#11222 / #11308 — a heavy startup before the
///   main window (Wrapp: splash → PS SDK → WebView2) makes Win11 cache the
///   DEFAULT icon; the fix is setting the window CLASS icon at source
///   initialization, before the taskbar snapshot.</description></item>
/// <item><description>Icon re-resolution after a session unlock or DPI change
///   can silently fail; re-sending WM_SETICON with fresh handles repairs the
///   taskbar entry.</description></item>
/// </list>
/// The HICONs are extracted once from the exe's icon group (which, since the
/// multi-frame burrito.ico rebuild, carries the full 16-256px size set) and
/// kept for process lifetime — never destroyed, so the shell can always
/// re-read them.
/// </summary>
internal static class TaskbarIconGuard
{
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;
    private const int WM_SETICON = 0x0080;
    private static readonly IntPtr IconSmall = IntPtr.Zero;
    private static readonly IntPtr IconBig = (IntPtr)1;

    private static IntPtr _hIconLarge;
    private static IntPtr _hIconSmall;
    private static bool _loadAttempted;
    private static Window? _sessionWindow;

    /// <summary>
    /// Call from a window's constructor. Sets the class + window icons as soon
    /// as the HWND exists (SourceInitialized) — early enough to beat the
    /// Win11 taskbar's default-icon snapshot.
    /// </summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = WindowHelper.GetWindowHandle(window);
                if (hwnd == IntPtr.Zero || !EnsureIcons()) return;
                SetClassIcon(hwnd, GCLP_HICON, _hIconLarge);
                SetClassIcon(hwnd, GCLP_HICONSM, _hIconSmall);
                SendIcons(hwnd);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"TaskbarIconGuard: attach failed -- {ex.Message}");
            }
        };
    }

    /// <summary>
    /// Re-sends WM_SETICON (big + small) — call after events that make the
    /// shell re-resolve icons (session unlock, DPI change).
    /// </summary>
    public static void Reassert(Window window)
    {
        try
        {
            var hwnd = WindowHelper.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero || !EnsureIcons()) return;
            SendIcons(hwnd);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"TaskbarIconGuard: reassert failed -- {ex.Message}");
        }
    }

    /// <summary>
    /// Re-asserts the icon whenever the workstation is unlocked. Subscribes the
    /// static SystemEvents hook once; unsubscribed when the window closes so
    /// the window object can't leak through the static event.
    /// </summary>
    public static void HookSessionUnlock(Window window)
    {
        _sessionWindow = window;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        window.Closed += (_, _) =>
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _sessionWindow = null;
        };
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock) return;
        if (_sessionWindow is not { } w) return;
        w.Dispatcher.BeginInvoke(() =>
        {
            AppLogger.Info("TaskbarIconGuard: session unlocked -- reasserting window icon");
            Reassert(w);
        });
    }

    private static void SendIcons(IntPtr hwnd)
    {
        SendMessage(hwnd, WM_SETICON, IconBig, _hIconLarge);
        SendMessage(hwnd, WM_SETICON, IconSmall, _hIconSmall);
    }

    private static bool EnsureIcons()
    {
        if (_hIconLarge != IntPtr.Zero) return true;
        if (_loadAttempted) return false;
        _loadAttempted = true;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var count = ExtractIconEx(exe, 0, out _hIconLarge, out _hIconSmall, 1);
        if (count <= 0 || _hIconLarge == IntPtr.Zero)
        {
            AppLogger.Warn("TaskbarIconGuard: could not extract icons from the exe icon group");
            return false;
        }
        if (_hIconSmall == IntPtr.Zero) _hIconSmall = _hIconLarge;
        return true;
    }

    private static void SetClassIcon(IntPtr hwnd, int index, IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return;
        if (IntPtr.Size == 8) SetClassLongPtr64(hwnd, index, hIcon);
        else SetClassLongPtr32(hwnd, index, hIcon.ToInt32());
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
    private static extern int SetClassLongPtr32(IntPtr hWnd, int nIndex, int dwNewLong);
}
