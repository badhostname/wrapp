using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class IntuneViewModel
{
    // -----------------------------------------------------------------------
    // Output: where this bundle's .intunewin is written. One file per run,
    // shared by every package, so it is a property of the bundle
    // (Script.IntunePackager.Output). The card shows what Settings resolve
    // to, always, and lets the bundle override it with a browsed folder,
    // nested under the Settings sub-folder format or used directly. The
    // stored Path is recomputed at every save (IntuneWinOutput.Apply in the
    // save pipeline); here it is written only when the operator changes the
    // choice, so opening a bundle never makes it dirty.
    // -----------------------------------------------------------------------

    private AppSection? _outputWiredApp;
    private int _outputProbe;

    private IntuneOutputSection Output => IntunePackager.Output;

    private string? BundleRootForOutput => _generalVm.IsTempWorkspace() ? null : _generalVm.BundleRootDir;

    public bool OutputIsCustom
    {
        get => string.Equals(Output.Source, IntuneWinOutput.SourceCustom, StringComparison.OrdinalIgnoreCase);
        set { if (value && !OutputIsCustom) SetOutputSource(IntuneWinOutput.SourceCustom); }
    }

    public bool OutputFromSettings
    {
        get => !OutputIsCustom;
        set { if (value && OutputIsCustom) SetOutputSource(IntuneWinOutput.SourceSettings); }
    }

    public bool OutputDirect
    {
        get => string.Equals(Output.Layout, IntuneWinOutput.LayoutDirect, StringComparison.OrdinalIgnoreCase);
        set { if (value && !OutputDirect) SetOutputLayout(IntuneWinOutput.LayoutDirect); }
    }

    public bool OutputNested
    {
        get => !OutputDirect;
        set { if (value && OutputDirect) SetOutputLayout(IntuneWinOutput.LayoutNested); }
    }

    public string OutputCustomRoot => Output.Root;

    /// <summary>A folder has been browsed for this bundle; the nested and direct previews show only then.</summary>
    public bool OutputHasCustomRoot => !string.IsNullOrWhiteSpace(Output.Root);

    /// <summary>"Beside the bundle" or "Central folder": what Settings say today.</summary>
    public string OutputSettingsModeLabel => IntuneWinOutput.ModeLabel(_appSettings);

    /// <summary>The folder Settings would give this bundle.</summary>
    public string OutputSettingsPath => DescribeOutput(IntuneWinOutput.FromSettings(_appSettings, App));

    public string OutputSubFolderFormat => string.IsNullOrWhiteSpace(_appSettings.IntuneWinSubFolderFormat)
        ? "(no sub-folder)"
        : _appSettings.IntuneWinSubFolderFormat.Trim();

    public string OutputNestedPreview => string.IsNullOrWhiteSpace(Output.Root)
        ? string.Empty
        : IntuneWinOutput.FromCustom(_appSettings, App, Output.Root, IntuneWinOutput.LayoutNested);

    public string OutputDirectPreview => string.IsNullOrWhiteSpace(Output.Root) ? string.Empty : Output.Root.Trim();

    /// <summary>The folder the next save will record and the next run will use.</summary>
    public string OutputResolvedPath => DescribeOutput(IntuneWinOutput.Compute(_appSettings, App, Output));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputWarningVisibility))]
    private string _outputWarning = string.Empty;

    public Visibility OutputWarningVisibility => string.IsNullOrEmpty(OutputWarning) ? Visibility.Collapsed : Visibility.Visible;

    private string DescribeOutput(string path)
    {
        var resolved = IntuneWinOutput.Resolve(path, BundleRootForOutput);
        return Path.IsPathRooted(resolved)
            ? resolved
            : $"{resolved}  (next to the version folder, once the bundle is saved)";
    }

    private void SetOutputSource(string source)
    {
        Output.Source = source;
        RefreshOutput(apply: true);
    }

    private void SetOutputLayout(string layout)
    {
        Output.Layout = layout;
        RefreshOutput(apply: true);
    }

    [RelayCommand]
    private void BrowseOutputRoot()
    {
        var folder = FileDialogService.BrowseFolder("Choose the folder for this bundle's .intunewin");
        if (string.IsNullOrEmpty(folder)) return;
        Output.Root = folder;
        Output.Source = IntuneWinOutput.SourceCustom;
        RefreshOutput(apply: true);
        AppLogger.Info($"Intune: output folder for this bundle -> {Output.Path}");
    }

    [RelayCommand]
    private void OpenOutputSettings() => _mainVm.NavigateCommand.Execute(NavigationSection.Settings);

    /// <summary>
    /// Re-reads the card from the model and Settings. With <paramref name="apply"/>
    /// the stored Path is recomputed too (an operator change); without it the
    /// card only shows what a save would write.
    /// </summary>
    private void RefreshOutput(bool apply)
    {
        if (apply) IntuneWinOutput.Apply(_appSettings, _generalVm.FullConfig);
        foreach (var name in new[]
                 {
                     nameof(OutputIsCustom), nameof(OutputFromSettings), nameof(OutputDirect), nameof(OutputNested),
                     nameof(OutputCustomRoot), nameof(OutputHasCustomRoot), nameof(OutputSettingsModeLabel), nameof(OutputSettingsPath),
                     nameof(OutputSubFolderFormat), nameof(OutputNestedPreview), nameof(OutputDirectPreview),
                     nameof(OutputResolvedPath),
                 })
            OnPropertyChanged(name);
        ProbeOutputReachability();
    }

    private void WireOutputToApp()
    {
        if (_outputWiredApp is not null) _outputWiredApp.PropertyChanged -= OnAppChangedForOutput;
        _outputWiredApp = App;
        _outputWiredApp.PropertyChanged += OnAppChangedForOutput;
    }

    private void OnAppChangedForOutput(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSection.Company) or nameof(AppSection.Name) or nameof(AppSection.Version)
            or nameof(AppSection.DotVersion) or nameof(AppSection.Language))
            RefreshOutput(apply: false);
    }

    /// <summary>
    /// Says so when the chosen folder's drive or share is not reachable from
    /// here. Probed off the UI thread: a dead share can take seconds to
    /// answer. The run would fall back to Output beside the bundle.
    /// </summary>
    private void ProbeOutputReachability()
    {
        var resolved = IntuneWinOutput.Resolve(IntuneWinOutput.Compute(_appSettings, App, Output), BundleRootForOutput);
        if (!Path.IsPathRooted(resolved))
        {
            OutputWarning = string.Empty;
            return;
        }
        var token = ++_outputProbe;
        var beside = IntuneWinOutput.BesideBundle(BundleRootForOutput);
        SafeFireAndForget.Run(async () =>
        {
            var reachable = await Task.Run(() => IntuneWinOutput.RootReachable(resolved));
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (token != _outputProbe) return;
                OutputWarning = reachable
                    ? string.Empty
                    : $"{Path.GetPathRoot(resolved)} is not reachable from this computer. A run from here would write to {beside} instead.";
            });
        }, "intune-output-probe");
    }
}
