using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.ViewModels;
using RadioButton = System.Windows.Controls.RadioButton;
using CloseReason = Wrapp.Services.CloseReason;   // WinForms also declares one
using Wpf.Ui.Controls;

namespace Wrapp.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel      _vm;
    private readonly GeneralViewModel   _generalVm;
    private readonly IntuneViewModel    _intuneVm;
    private readonly SCCMViewModel      _sccmVm;
    private readonly DetectionViewModel _detectionVm;
    private readonly ScriptsViewModel   _scriptsVm;
    private readonly ConfigJsonViewModel _configJsonVm;
    private readonly RunViewModel       _runVm;
    private readonly LogsViewModel      _logsVm;
    private readonly GitHistoryViewModel _gitHistoryVm;
    private readonly InventoryViewModel _inventoryVm;
    private readonly ToolsViewModel     _toolsVm;
    private readonly SettingsViewModel  _settingsVm;

    private bool _closeConfirmed;
    private bool _closePending;

    /// <summary>
    /// THE close pipeline: every close entry point - the X button,
    /// the update handoff, a sibling instance's close request - runs
    /// <see cref="CloseGuard.RunAsync"/> once, with the same cancellable
    /// prompts. The mandatory-close machinery this replaces is gone: updates
    /// are never enforced mid-session, so staying open is always an option.
    /// </summary>
    private readonly CloseGuard _closeGuard;

    /// <summary>
    /// Public close entry for non-user reasons (update handoff, sibling
    /// request). Mirrors the Closing handler's kickoff, with the reason's
    /// context line on the prompts.
    /// </summary>
    public void BeginClose(CloseReason reason)
    {
        if (_closeConfirmed || _closePending) return;
        _closePending = true;
        _ = RunCloseAsync(reason);
    }

    // Section view instances (created once, reused on re-navigation)
    private GeneralView?    _generalView;
    private IntuneView?     _intuneView;
    private SCCMView?       _sccmView;
    private DetectionView?  _detectionView;
    private ScriptsView?    _scriptsView;
    private ConfigJsonView? _configJsonView;
    private RunView?        _runView;
    private LogsView?       _logsView;
    private GitHistoryView? _gitHistoryView;
    private InventoryView?  _inventoryView;
    private ToolsView?      _toolsView;
    private SettingsView?   _settingsView;

    public MainWindow(
        MainViewModel      vm,
        GeneralViewModel   generalVm,
        IntuneViewModel    intuneVm,
        SCCMViewModel      sccmVm,
        DetectionViewModel detectionVm,
        ScriptsViewModel   scriptsVm,
        ConfigJsonViewModel configJsonVm,
        RunViewModel       runVm,
        LogsViewModel      logsVm,
        GitHistoryViewModel gitHistoryVm,
        InventoryViewModel inventoryVm,
        ToolsViewModel     toolsVm,
        SettingsViewModel  settingsVm)
    {
        InitializeComponent();
        _vm           = vm;
        _generalVm    = generalVm;
        _intuneVm     = intuneVm;
        _sccmVm       = sccmVm;
        _detectionVm  = detectionVm;
        _scriptsVm    = scriptsVm;
        _configJsonVm = configJsonVm;
        _runVm        = runVm;
        _logsVm       = logsVm;
        _gitHistoryVm = gitHistoryVm;
        _inventoryVm  = inventoryVm;
        _toolsVm      = toolsVm;
        _settingsVm   = settingsVm;
        DataContext   = vm;

        Loaded += (_, _) =>
        {
            FluentDialog.SetHost(RootDialogHost);
            NavGeneral.IsChecked = true;
            ContentArea.Content = GetOrCreatePage(NavigationSection.General);
            // Module init (Import-Module + LoadDefaults) runs in the background
            // so the window is immediately interactive. It completes before the
            // user navigates to Run or validates. SafeFireAndForget surfaces
            // any module-load failure to app.log instead of vanishing.
            SafeFireAndForget.Run(() => vm.InitializeCommand.ExecuteAsync(null), "MainWindow.Initialize");

        };

        // Right-align the account popup with the sign-in button
        AccountPopup.Opened += (_, _) =>
        {
            if (AccountPopup.Child is FrameworkElement child)
            {
                child.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                AccountPopup.HorizontalOffset = AccountButton.ActualWidth - child.DesiredSize.Width;
            }
        };

        // One close pipeline. Every gate - background jobs, running
        // transfer, dirty bundle, dirty settings - lives in CloseGuard and is
        // walked exactly once per attempt (the old handler re-entered Close()
        // per satisfied barrier, traversing this event up to four times).
        _closeGuard = new CloseGuard(
            new FluentCloseInteraction(),
            new CloseGuard.Jobs(
                HasActive:            () => _vm.JobTracker.HasActiveJobs,
                ActiveCount:          () => _vm.JobTracker.ActiveCount,
                MarkShuttingDown:     () => _vm.JobTracker.IsShuttingDown = true,
                RevertShutdown:       () => _vm.JobTracker.RevertShutdown(),
                CancelAllAndWaitAsync: async wait =>
                {
                    _vm.JobTracker.CancelAll();
                    await _vm.JobTracker.WaitAllAsync(wait);
                }),
            isTransferring: () => _vm.IsTransferring,
            scopes: new[]
            {
                new CloseGuard.Scope(
                    "bundle", "Save Progress",
                    "You have unsaved changes.\n\nSave the bundle before closing?",
                    () => _vm.IsDirty, SaveBundleAsync),
                new CloseGuard.Scope(
                    "settings", "Save Settings",
                    "You have unsaved Settings or Preferences changes.\n\nSave them before closing?",
                    () => _settingsVm.IsDirty, SaveSettingsAsync),
            });

        Closing += (_, e) =>
        {
            if (_closeConfirmed) return;
            e.Cancel = true;
            if (_closePending) return;
            _closePending = true;
            _ = RunCloseAsync(CloseReason.UserClose);
        };

        // Monitor/DPI changes (docking, undocking, OS scale change) don't raise
        // SizeChanged or IsVisibleChanged on the WebView2 hosts, so Monaco's
        // automaticLayout keeps the stale viewport metrics. Force a relayout
        // on DpiChanged so text rendering and top-left origin are recomputed.
        DpiChanged += OnWindowDpiChanged;

        // Taskbar icon protection: class icon set at HWND creation (beats the
        // Win11 default-icon snapshot during our heavy startup) + reassert on
        // session unlock. See TaskbarIconGuard.
        Helpers.TaskbarIconGuard.Attach(this);
        Helpers.TaskbarIconGuard.HookSessionUnlock(this);

        // The update handoff closes through the same CloseGuard pipeline as
        // any close - with its context line, and with Cancel intact.
        Services.UpdateFlowController.TryCloseMainForUpdateAsync = TryCloseForUpdateAsync;

        // Pause the bundle dirty-check serializer while the
        // window is in the background (edits require an active window).
        Activated   += (_, _) => _generalVm.SetChangeTrackingActive(true);
        Deactivated += (_, _) => _generalVm.SetChangeTrackingActive(false);
    }

    /// <summary>
    /// Update-handoff close: the same CloseGuard walk as any close
    /// - Cancel aborts the update - but on Proceed the app must SURVIVE this
    /// window's death, so the handoff is marked and the shutdown mode
    /// switched BEFORE Close(). Returns false when the user kept the window
    /// open (the pending update stays pending).
    /// </summary>
    private async Task<bool> TryCloseForUpdateAsync()
    {
        if (_closeConfirmed) return true;
        if (_closePending) return false;
        _closePending = true;
        try
        {
            var outcome = await _closeGuard.RunAsync(CloseReason.UpdateHandoff);
            if (!outcome.Proceed) return false;

            if (!outcome.SavedScopeIds.Contains("bundle") && _generalVm.IsTempWorkspace())
                TempWorkspaceService.DeleteWorkspace(_generalVm.BundleRootDir);

            Services.UpdateFlowController.MarkHandoffActive();
            System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _closeConfirmed = true;
            Close();
            return true;
        }
        finally { _closePending = false; }
    }

    private async void OnWindowDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        AppLogger.Info($"MainWindow: DpiChanged Old={e.OldDpi.DpiScaleX:F3}x{e.OldDpi.DpiScaleY:F3} -> New={e.NewDpi.DpiScaleX:F3}x{e.NewDpi.DpiScaleY:F3}; Window.Size={ActualWidth:F0}x{ActualHeight:F0}");
        // The shell re-resolves taskbar icons on DPI transitions; re-send ours
        // so a failed re-resolution can't leave the generic glyph.
        Helpers.TaskbarIconGuard.Reassert(this);
        if (_scriptsVm.TabService is not null)
            await _scriptsVm.TabService.LayoutAsync(force: true);
        if (_configJsonVm.Monaco is not null)
            await _configJsonVm.Monaco.LayoutAsync(force: true);
    }

    // ------------------------------------------------------------------
    // Win32 minimum size enforcement
    // ------------------------------------------------------------------
    // FluentWindow uses custom WindowChrome, which can bypass the standard
    // WPF MinWidth/MinHeight during resize. Hooking WM_GETMINMAXINFO at
    // the Win32 level guarantees the OS enforces our minimum size before
    // any WPF layout occurs, so chrome buttons (min/max/close) can never
    // be pushed off-screen.

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            // Convert DIP (device-independent pixels) to physical pixels
            var source = PresentationSource.FromVisual(this);
            double dpiX = 1.0, dpiY = 1.0;
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }

            mmi.ptMinTrackSize.x = (int)(MinWidth * dpiX);
            mmi.ptMinTrackSize.y = (int)(MinHeight * dpiY);

            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // ------------------------------------------------------------------

    private async void JobsButton_Click(object sender, RoutedEventArgs e)
    {
        var jobs = _vm.JobTracker.Jobs;
        var panel = new StackPanel { MaxWidth = 700 };

        // Scope jobs to the active bundle + app-wide (empty bundle) jobs.
        var bundleRoot = _generalVm.BundleRootDir ?? string.Empty;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(jobs);
        view.Filter = o =>
        {
            if (o is not Models.BackgroundJob j) return false;
            if (string.IsNullOrEmpty(j.BundleRootDir)) return true;                     // app-wide jobs always visible
            if (string.IsNullOrEmpty(bundleRoot))      return true;                     // no bundle active -> show all
            return string.Equals(j.BundleRootDir, bundleRoot, StringComparison.OrdinalIgnoreCase);
        };

        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Current and recent background jobs for this bundle. Packaging runs expand to the deployment tree.",
            FontSize = 12,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Foreground = FindBrush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var headerActions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Info button - opens the Help.BackgroundJobs.Overview popup so the
        // dialog has the same parent-help entry point as every view-level header.
        var infoButton = new System.Windows.Controls.Button
        {
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = TryFindResource("Help.BackgroundJobs.InfoButton") as string,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "\uE946",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = FindBrush("TextMutedBrush"),
            },
        };
        infoButton.Click += async (_, __) =>
        {
            var help = TryFindResource("Help.BackgroundJobs.Overview") as string;
            if (string.IsNullOrEmpty(help)) return;
            var helpPanel = Controls.SectionHeader.BuildFormattedPanel(help, this);
            await FluentDialog.ShowScrollableContentAsync("Background Jobs", helpPanel, "Close");
        };
        headerActions.Children.Add(infoButton);

        var clearButton = new System.Windows.Controls.Button
        {
            Content = "Clear completed",
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = TryFindResource("Help.BackgroundJobs.ClearCompleted") as string,
        };
        clearButton.Click += (_, __) => _vm.JobTracker.ClearCompleted();
        headerActions.Children.Add(clearButton);
        Grid.SetColumn(headerActions, 1);
        headerRow.Children.Add(headerActions);
        panel.Children.Add(headerRow);

        var itemsControl = new ItemsControl { ItemsSource = view };
        itemsControl.ItemTemplate = (DataTemplate)FindResource("JobCardTemplate");
        panel.Children.Add(itemsControl);

        var emptyPanel = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 20)
        };
        emptyPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "\uE9D5",
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 36, Foreground = FindBrush("TextMutedBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Opacity = 0.4
        });
        emptyPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "No background jobs for this bundle", FontSize = 13,
            Foreground = FindBrush("TextMutedBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        bool FilteredEmpty() => !view.Cast<object>().Any();
        void UpdateEmpty(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs a) =>
            emptyPanel.Visibility = FilteredEmpty() ? Visibility.Visible : Visibility.Collapsed;
        jobs.CollectionChanged += UpdateEmpty;
        emptyPanel.Visibility = FilteredEmpty() ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(emptyPanel);

        // Scrollable wrapper: long job lists get the app's gentle wheel
        // instead of the ContentDialog template viewer's raw fast scroll.
        await FluentDialog.ShowScrollableContentAsync("Background Jobs", panel, "Close");
        jobs.CollectionChanged -= UpdateEmpty;
        view.Filter = null;
    }


    private static System.Windows.Media.Brush FindBrush(string key) =>
        Application.Current.TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Gray;

    /// <summary>
    /// Status-bar "About Wrapp" info button - opens a FluentDialog with a
    /// splash-style header (large icon + app name + version) followed by the
    /// Markdown-rendered <c>Help.About.Overview</c> body. Mirrors the
    /// SplashWindow's Row 1 logo block so the About popup reads as the
    /// app's identity card, not just another help page.
    /// </summary>
    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var help = TryFindResource("Help.About.Overview") as string;
        if (string.IsNullOrEmpty(help)) return;

        var panel = new System.Windows.Controls.StackPanel { MaxWidth = 580 };

        // Splash-style identity header: 72px icon + "Wrapp" 26pt SemiBold +
        // version subtitle. Matches the SplashWindow Row 1 layout.
        var headerStack = new System.Windows.Controls.StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 16),
        };
        headerStack.Children.Add(new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/burrito.png", UriKind.Absolute)),
            Width = 72,
            Height = 72,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
        });
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(
            headerStack.Children[^1],
            System.Windows.Media.BitmapScalingMode.HighQuality);
        headerStack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Wrapp",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("TextPrimaryBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        });
        headerStack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = AppInfo.VersionDisplay,
            FontSize = 13,
            Foreground = FindBrush("TextSecondaryBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        });
        panel.Children.Add(headerStack);

        // Markdown body (same renderer / spacing as every other help popup).
        var helpPanel = Controls.SectionHeader.BuildFormattedPanel(help, this);
        panel.Children.Add(helpPanel);

        // Recent changes: the What's-New version cards for the latest releases,
        // followed by a link to the complete history (the embedded changelog is
        // capped here so About opens fast). Same cards as the update popup.
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "What's new",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("TextPrimaryBrush"),
            Margin = new Thickness(0, 16, 0, 8),
        });
        panel.Children.Add(Services.WhatsNewService.BuildRecentChangesPanel(this));
        panel.Children.Add(Services.WhatsNewService.BuildReleasesLink(this));

        // Host in the shared padded, gently-scrolling viewport so the scrollbar
        // is clear of the text and the wheel feel matches the rest of the app.
        await FluentDialog.ShowScrollableContentAsync("About Wrapp", panel, "Close");
    }

    /// <summary>
    /// The one close path: run the guard; on Proceed, clean up a temp
    /// workspace that wasn't just saved (a clean or discarded draft is
    /// deleted; a draft the user saved in THIS close survives), then close
    /// for real.
    /// </summary>
    private async Task RunCloseAsync(CloseReason reason)
    {
        try
        {
            var outcome = await _closeGuard.RunAsync(reason);
            if (!outcome.Proceed) return;

            if (!outcome.SavedScopeIds.Contains("bundle") && _generalVm.IsTempWorkspace())
                TempWorkspaceService.DeleteWorkspace(_generalVm.BundleRootDir);

            _closeConfirmed = true;
            Close();
        }
        finally { _closePending = false; }
    }

    /// <summary>CloseGuard's UI surface, implemented over FluentDialog.</summary>
    private sealed class FluentCloseInteraction : CloseGuard.IInteraction
    {
        public Task<bool> ConfirmCancelJobsAsync(int activeCount, string context)
            => FluentDialog.ShowSelectAsync(
                "Operations Running",
                new System.Windows.Controls.TextBlock
                {
                    Text = context + $"{activeCount} background operation(s) are still running. Cancel all and close?",
                    TextWrapping = System.Windows.TextWrapping.Wrap
                },
                "Close", "Cancel");

        public Task NotifyTransferInProgressAsync()
            => FluentDialog.ShowInfoAsync(
                "Transfer In Progress",
                "A file transfer is currently running. Please wait for it to complete before closing.");

        public async Task<CloseGuard.SaveChoice> AskSaveAsync(string title, string message)
            => await FluentDialog.SaveDiscardCancelAsync(title, message) switch
            {
                "Save"    => CloseGuard.SaveChoice.Save,
                "Discard" => CloseGuard.SaveChoice.Discard,
                _         => CloseGuard.SaveChoice.Cancel,
            };
    }

    /// <summary>Runs the bundle save command (no-op when unavailable).</summary>
    private async Task SaveBundleAsync()
    {
        if (_vm.SaveBundleCommand is not null)
            await _vm.SaveBundleCommand.ExecuteAsync(null);
    }

    /// <summary>Runs the settings save command (no-op when unavailable).</summary>
    private async Task SaveSettingsAsync()
    {
        if (_settingsVm.SaveCommand is not null)
            await _settingsVm.SaveCommand.ExecuteAsync(null);
    }

    private void TitleDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton btn) return;
        if (!Enum.TryParse<NavigationSection>(btn.Tag?.ToString(), out var section)) return;

        _vm.CurrentSection = section;
        ContentArea.Content = GetOrCreatePage(section);
    }

    /// <summary>Hamburger: toggles the sidebar between labeled rows (168px)
    /// and centred icons only (54px). Rows are a fixed 42px tall with the
    /// badges overlaid rather than laid out, so no button changes size when
    /// a count appears or clears, and collapsed rows centre their icon with
    /// the badge stack riding its trailing edge - no reserved lane. The
    /// NavItem/NavLabel/NavBadgeStack styles react to
    /// <see cref="MainViewModel.IsNavCollapsed"/>; the column width is the
    /// only view-side concern.</summary>
    private void NavCollapse_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsNavCollapsed = !_vm.IsNavCollapsed;
        SidebarColumn.Width = new GridLength(_vm.IsNavCollapsed ? 54 : 168);
    }

    private void NavigateToSection(NavigationSection section)
    {
        var buttons = new Dictionary<NavigationSection, RadioButton>
        {
            [NavigationSection.General]    = NavGeneral,
            [NavigationSection.Intune]     = NavIntune,
            [NavigationSection.SCCM]       = NavSCCM,
            [NavigationSection.Detection]  = NavDetection,
            [NavigationSection.Scripts]    = NavScripts,
            [NavigationSection.ConfigJson] = NavConfigJson,
            [NavigationSection.Run]        = NavRun,
            [NavigationSection.Inventory]  = NavInventory,
            [NavigationSection.Logs]       = NavLogs,
            [NavigationSection.GitHistory] = NavGitHistory,
            [NavigationSection.Settings]   = NavSettings,
        };

        if (!buttons.TryGetValue(section, out var btn)) return;

        btn.IsChecked = true;
        _vm.CurrentSection = section;
        ContentArea.Content = GetOrCreatePage(section);
    }

    private object? GetOrCreatePage(NavigationSection section)
    {
        // Enterprise policy: a hidden section is unreachable even
        // programmatically - the rail button is collapsed, but keyboard/
        // startup/restored-state paths land here too.
        if (Services.Policy.PolicyService.Current.IsSectionHidden(section))
        {
            Services.AppLogger.Warn($"Policy: navigation to hidden section '{section}' redirected to General");
            section = NavigationSection.General;
        }

        Services.UiTrace.Log($"nav: {section}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = CreatePageCore(section);
        // First construction of a heavy view (Settings is 1,800+ XAML lines)
        // is a real dispatcher cost; log it ALWAYS when noticeable so slow
        // navigation and stall reports carry numbers without the trace on.
        if (sw.ElapsedMilliseconds > 100)
            Services.AppLogger.Info($"[PERF] view built: {section} in {sw.ElapsedMilliseconds}ms");
        return page;
    }

    private object? CreatePageCore(NavigationSection section)
    {
        switch (section)
        {
            case NavigationSection.General:
                _generalView ??= new GeneralView { DataContext = _generalVm };
                return _generalView;

            case NavigationSection.Intune:
                _intuneView ??= new IntuneView { DataContext = _intuneVm };
                return _intuneView;

            case NavigationSection.SCCM:
                _sccmView ??= new SCCMView { DataContext = _sccmVm };
                return _sccmView;

            case NavigationSection.Detection:
                _detectionView ??= new DetectionView { DataContext = _detectionVm };
                return _detectionView;

            case NavigationSection.Scripts:
                _scriptsView ??= new ScriptsView { DataContext = _scriptsVm };
                return _scriptsView;

            case NavigationSection.ConfigJson:
                _configJsonView ??= new ConfigJsonView { DataContext = _configJsonVm };
                return _configJsonView;

            case NavigationSection.Run:
                _runView ??= new RunView { DataContext = _runVm };
                return _runView;

            case NavigationSection.Inventory:
                _inventoryView ??= new InventoryView { DataContext = _inventoryVm };
                return _inventoryView;

            case NavigationSection.Tools:
                _toolsView ??= new ToolsView { DataContext = _toolsVm };
                return _toolsView;

            case NavigationSection.Logs:
                _logsView ??= new LogsView { DataContext = _logsVm };
                return _logsView;

            case NavigationSection.GitHistory:
                _gitHistoryView ??= new GitHistoryView { DataContext = _gitHistoryVm };
                return _gitHistoryView;

            case NavigationSection.Settings:
                _settingsView ??= new SettingsView { DataContext = _settingsVm };
                return _settingsView;

            default:
                return null;
        }
    }
}
