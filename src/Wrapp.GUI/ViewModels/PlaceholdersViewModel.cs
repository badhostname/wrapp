using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

/// <summary>
/// The Settings → Placeholders tab. One grid mixing
/// read-only BUILT-IN rows (live values resolved from the active bundle's
/// <see cref="AppSection"/>) with editable CUSTOM rows, plus the two
/// read-only "effective configuration" viewers and the log-redaction summary.
///
/// <para>Persistence rides the existing Settings save flow:
/// <see cref="SettingsViewModel.SaveAsync"/> calls
/// <see cref="ApplyToSettingsAsync"/> before writing settings.json. Sensitive
/// values go to the DPAPI sidecar (<see cref="PlaceholderSecureStore"/>) and
/// the persisted row keeps an EMPTY Value - the tenant-ClientSecret rule.</para>
/// </summary>
public partial class PlaceholdersViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    /// <summary>Built-in rows first (grayed, read-only), then custom rows.</summary>
    public ObservableCollection<PlaceholderRowVm> Rows { get; } = new();

    /// <summary>Drives IsEnabled on the "Remove selected" button (customs only).</summary>
    public SelectionTracker<PlaceholderRowVm> RowsSelection { get; }
        = new(r => r.IsSelected);

    /// <summary>
    /// Inline validation error - same style as the Detection view's
    /// duplicate-symbol warning: invalid/reserved/duplicate names are called
    /// out here AND block the whole Settings save until fixed
    /// (<see cref="SettingsViewModel.SaveAsync"/> gates on <see cref="ErrorCount"/>).
    /// </summary>
    [ObservableProperty] private string _nameWarning = string.Empty;
    [ObservableProperty] private bool _hasNameWarning;

    /// <summary>
    /// Number of custom rows whose name is invalid, reserved, or duplicated.
    /// Drives the red badges (Settings nav item + Placeholders tab header)
    /// and the save block. Recomputed on every Name/IsSensitive edit and on
    /// row add/remove - the Detection duplicate-symbol pipeline, applied here.
    /// </summary>
    [ObservableProperty] private int _errorCount;

    // ------------------------------------------------------------------
    // Effective configuration viewers (read-only; no Monaco - plain text)
    // ------------------------------------------------------------------

    [ObservableProperty] private string _preferencesJson = string.Empty;
    [ObservableProperty] private string _orgDefaultsPath = string.Empty;
    [ObservableProperty] private string _orgDefaultsContent = string.Empty;

    // ------------------------------------------------------------------
    // Log-redaction summary (AppLogger.GetActiveRedactionSummary)
    // ------------------------------------------------------------------

    public ObservableCollection<string> RedactionBuiltIns { get; } = new();
    public ObservableCollection<string> RedactionOrgPatterns { get; } = new();
    [ObservableProperty] private string _redactionOrgSource = string.Empty;
    [ObservableProperty] private bool _hasOrgPatterns;
    [ObservableProperty] private string _redactionSensitiveSummary = string.Empty;

    private AppSection? _observedApp;

    public PlaceholdersViewModel(AppSettings settings)
    {
        _settings = settings;
        LoadFromSettings();
        RowsSelection.Bind(Rows);
        RefreshPreferencesJson();
        RefreshOrgDefaults();
        RefreshRedaction();
    }

    // ------------------------------------------------------------------
    // Live built-in values: CompositionRoot points this at the active
    // bundle's AppSection (re-pointed on every config load) so the grayed
    // rows update in real time as the General view edits fields.
    // ------------------------------------------------------------------

    /// <summary>
    /// Observes <paramref name="app"/> for changes and refreshes the built-in
    /// rows. Safe to call repeatedly; re-pointing unsubscribes the old section.
    /// </summary>
    public void ObserveApp(AppSection app)
    {
        if (!ReferenceEquals(_observedApp, app))
        {
            if (_observedApp is not null)
                _observedApp.PropertyChanged -= OnAppPropertyChanged;
            _observedApp = app;
            app.PropertyChanged += OnAppPropertyChanged;
        }
        RefreshBuiltIns();
    }

    private void OnAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => RefreshBuiltIns();

    /// <summary>Recomputes the built-in rows' live values in place.</summary>
    public void RefreshBuiltIns()
    {
        var snapshot = PlaceholderService.Snapshot(_observedApp)
            .Where(p => p.IsBuiltIn)
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows.Where(r => r.IsBuiltIn))
        {
            if (snapshot.TryGetValue(row.Name, out var value))
                row.Value = value;
        }
    }

    // ------------------------------------------------------------------
    // Load / save
    // ------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the grid from <see cref="AppSettings.Placeholders"/> (call
    /// after import / reset, mirroring <see cref="PreferencesViewModel.LoadFromSettings"/>).
    /// </summary>
    public void LoadFromSettings()
    {
        foreach (var row in Rows)
            row.PropertyChanged -= OnRowPropertyChanged;
        Rows.Clear();

        foreach (var name in PlaceholderService.BuiltInNames)
            AddRow(new PlaceholderRowVm(isBuiltIn: true) { Name = name });

        var storedNames = new HashSet<string>(
            PlaceholderSecureStore.Names(), StringComparer.OrdinalIgnoreCase);
        foreach (var saved in _settings.Placeholders)
        {
            AddRow(new PlaceholderRowVm(isBuiltIn: false)
            {
                Name            = saved.Name,
                Value           = saved.Value,
                IsSensitive     = saved.IsSensitive,
                Comment         = saved.Comment,
                HasStoredSecret = saved.IsSensitive && storedNames.Contains(saved.Name),
            });
        }

        RefreshBuiltIns();
        ValidateNames();
    }

    private void AddRow(PlaceholderRowVm row)
    {
        row.PropertyChanged += OnRowPropertyChanged;
        Rows.Add(row);
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaceholderRowVm.Name) or nameof(PlaceholderRowVm.IsSensitive))
            ValidateNames();
    }

    /// <summary>
    /// Stable JSON of the editable state (custom rows only - built-in rows are
    /// derived and would flip the dirty flag as the bundle changes). Feeds
    /// <see cref="SettingsViewModel"/>'s snapshot-diff dirty tracking.
    /// </summary>
    public string SerializeSnapshot()
    {
        var shape = Rows.Where(r => !r.IsBuiltIn).Select(r => new
        {
            r.Name,
            r.Value,
            r.IsSensitive,
            r.Comment,
            HasPendingSecret = r.PendingSecretValue.Length > 0,
            r.HasStoredSecret,
        }).ToArray();
        return JsonSerializer.Serialize(shape);
    }

    /// <summary>
    /// Persists the custom rows into <see cref="AppSettings.Placeholders"/>:
    /// plain values inline, sensitive values DPAPI-encrypted into the sidecar
    /// with the persisted Value left EMPTY; stale ciphertext pruned; the
    /// current sensitive plaintexts re-registered for log redaction.
    /// <para>Rows in error never reach this method - <see cref="SettingsViewModel.SaveAsync"/>
    /// aborts the whole save while <see cref="ErrorCount"/> &gt; 0. The
    /// name-validity/dedup guard below is defense in depth only. Returns
    /// false when DPAPI is unavailable - nothing is persisted in that case.</para>
    /// </summary>
    public async Task<bool> ApplyToSettingsAsync()
    {
        var persisted = new List<PlaceholderEntry>();
        var pendingWrites = new List<(PlaceholderRowVm Row, string Plaintext)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Rows.Where(r => !r.IsBuiltIn))
        {
            var name = (row.Name ?? string.Empty).Trim();
            if (!PlaceholderService.IsValidCustomName(name) || !seen.Add(name)) continue;

            if (row.IsSensitive)
            {
                // A fresh PasswordBox value wins; a plain Value left over from
                // toggling the Sensitive flag is treated as the new secret.
                var plaintext = row.PendingSecretValue.Length > 0 ? row.PendingSecretValue
                              : row.Value.Length > 0             ? row.Value
                              : null;
                if (plaintext is not null)
                    pendingWrites.Add((row, plaintext));
                persisted.Add(new PlaceholderEntry
                    { Name = name, Value = string.Empty, IsSensitive = true, Comment = row.Comment });
            }
            else
            {
                persisted.Add(new PlaceholderEntry
                    { Name = name, Value = row.Value, IsSensitive = false, Comment = row.Comment });
            }
        }

        try
        {
            foreach (var (row, plaintext) in pendingWrites)
            {
                PlaceholderSecureStore.SetValue(row.Name.Trim(), plaintext);
                row.PendingSecretValue = string.Empty;
                row.Value = string.Empty;
                row.HasStoredSecret = true;
            }
        }
        catch (SecretEncryptionException ex)
        {
            AppLogger.Warn($"Placeholders: sensitive value not saved -- {ex.Message}");
            await FluentDialog.ShowWarningAsync(
                "Placeholders not saved",
                $"A sensitive placeholder value could not be encrypted:\n\n{ex.Message}\n\n" +
                "Windows data protection (DPAPI) is required for sensitive values. " +
                "Your placeholder edits remain in the editor; nothing was persisted.");
            return false;
        }

        _settings.Placeholders = persisted;
        PlaceholderSecureStore.PruneExcept(
            persisted.Where(p => p.IsSensitive).Select(p => p.Name));
        // Resolver + redaction re-wired together (see RefreshFromSettings).
        PlaceholderService.RefreshFromSettings(_settings);

        AppLogger.Info($"Placeholders: saved {persisted.Count} custom placeholder(s) " +
                       $"({persisted.Count(p => p.IsSensitive)} sensitive)");
        return true;
    }

    // ------------------------------------------------------------------
    // Validation (errors, not warnings: offending rows BLOCK the save)
    // ------------------------------------------------------------------

    private void ValidateNames()
    {
        var problems = new List<string>();
        var errorRows = 0;

        // First pass: which trimmed names occur more than once (case-insensitive)?
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows.Where(r => !r.IsBuiltIn))
        {
            var name = (row.Name ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
        }

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows.Where(r => !r.IsBuiltIn))
        {
            var name = (row.Name ?? string.Empty).Trim();
            var display = name.Length == 0 ? "(empty)" : name;

            bool inError;
            if (name.Length == 0)
            {
                inError = true;
                if (reported.Add(display))
                    problems.Add("a row has no name");
            }
            else if (PlaceholderService.IsReservedName(name))
            {
                inError = true;
                if (reported.Add(name))
                    problems.Add($"\"{name}\" is a built-in name");
            }
            else if (!PlaceholderService.IsValidCustomName(name))
            {
                inError = true;
                if (reported.Add(name))
                    problems.Add($"\"{name}\" is not a valid name (letters, digits, - and _ only, max 64)");
            }
            else if (counts.TryGetValue(name, out var n) && n > 1)
            {
                inError = true;
                if (reported.Add(name))
                    problems.Add($"\"{name}\" is duplicated");
            }
            else
            {
                inError = false;
            }

            row.IsDuplicate = inError;
            if (inError) errorRows++;
        }

        ErrorCount = errorRows;
        HasNameWarning = problems.Count > 0;
        NameWarning = problems.Count > 0
            ? $"Fix before saving: {string.Join("; ", problems)}."
            : string.Empty;
    }

    /// <summary>
    /// Body of the save-blocked warning dialog: names every offending row.
    /// Separated from the dialog call so the block decision and its message
    /// are testable without UI.
    /// </summary>
    public string BuildBlockingErrorMessage()
        => "Placeholder names must be fixed before Settings can be saved.\n\n" +
           NameWarning.TrimEnd('.') + ".\n\n" +
           "Placeholder names must be unique (case-insensitive), use only letters, " +
           "digits, - and _ (max 64), and may not shadow a built-in name. " +
           "Correct or remove the flagged rows in Settings > Placeholders, then Save again.";

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    [RelayCommand]
    private void AddPlaceholder()
    {
        AddRow(new PlaceholderRowVm(isBuiltIn: false) { Name = "new-placeholder" });
        // Revalidate immediately (Detection's AddTest → ValidateSymbols shape):
        // a second untouched "new-placeholder" row is already a duplicate.
        ValidateNames();
    }

    [RelayCommand]
    private void RemoveSelectedPlaceholders()
    {
        foreach (var row in Rows.Where(r => r.IsSelected && !r.IsBuiltIn).ToList())
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            Rows.Remove(row);
        }
        ValidateNames();
    }

    [RelayCommand]
    public void RefreshPreferencesJson()
    {
        try
        {
            PreferencesJson = SettingsPortability.BuildExportJson(_settings);
        }
        catch (Exception ex)
        {
            PreferencesJson = $"Could not render preferences: {ex.Message}";
        }
    }

    [RelayCommand]
    public void RefreshOrgDefaults()
    {
        var path = DefaultsLoader.FindDefaultsFile();
        if (path is null)
        {
            OrgDefaultsPath = "No organization defaults file found";
            OrgDefaultsContent = string.Empty;
            return;
        }

        OrgDefaultsPath = path;
        try
        {
            OrgDefaultsContent = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            OrgDefaultsContent = $"Could not read file: {ex.Message}";
        }
    }

    /// <summary>Re-reads the active redaction summary (built-ins never change; org set can).</summary>
    public void RefreshRedaction()
    {
        var (builtIn, orgPatterns, sensitiveCount) = AppLogger.GetActiveRedactionSummary();

        RedactionBuiltIns.Clear();
        foreach (var category in builtIn)
            RedactionBuiltIns.Add(category);

        RedactionOrgPatterns.Clear();
        foreach (var pattern in orgPatterns)
            RedactionOrgPatterns.Add(pattern);
        HasOrgPatterns = orgPatterns.Length > 0;

        var source = DefaultsLoader.FindDefaultsFile();
        RedactionOrgSource = orgPatterns.Length == 0
            ? "No organization patterns active."
            : $"Source: {source ?? "organization defaults (file no longer present)"}";

        RedactionSensitiveSummary = sensitiveCount == 0
            ? "No sensitive placeholder values registered."
            : $"{sensitiveCount} sensitive placeholder value(s) registered - their plaintext is scrubbed from every log line.";
    }
}

/// <summary>
/// One grid row. Built-in rows are read-only with live values; custom rows
/// are fully editable. For sensitive rows the typed secret lives in
/// <see cref="PendingSecretValue"/> (fed by the PasswordBox code-behind, like
/// the tenant Client Secret column) until Save encrypts it into the sidecar.
/// </summary>
public partial class PlaceholderRowVm : ObservableObject
{
    public PlaceholderRowVm(bool isBuiltIn) => IsBuiltIn = isBuiltIn;

    public bool IsBuiltIn { get; }
    public bool IsCustom => !IsBuiltIn;
    public string Kind => IsBuiltIn ? "Built-in" : "Custom";

    /// <summary>UI-only selection state for the Remove-selected pattern.</summary>
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _name = string.Empty;

    /// <summary>
    /// Built-in rows: the LIVE resolved value. Custom plain rows: the stored
    /// value. Custom sensitive rows: always empty (plaintext never round-trips
    /// back into the UI; the sidecar owns it).
    /// </summary>
    [ObservableProperty] private string _value = string.Empty;

    [ObservableProperty] private bool _isSensitive;
    [ObservableProperty] private string _comment = string.Empty;

    /// <summary>True when the DPAPI sidecar holds a value for this name.</summary>
    [ObservableProperty] private bool _hasStoredSecret;

    /// <summary>
    /// True when this row's name is duplicated, reserved, or invalid - drives
    /// the red border on the Name cell (the Detection IsSymbolDuplicate
    /// pattern) and counts into <see cref="PlaceholdersViewModel.ErrorCount"/>.
    /// </summary>
    [ObservableProperty] private bool _isDuplicate;

    /// <summary>Transient plaintext typed into the PasswordBox; consumed on Save.</summary>
    [ObservableProperty] private string _pendingSecretValue = string.Empty;
}
