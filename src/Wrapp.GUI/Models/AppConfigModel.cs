using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wrapp.Models;

// ============================================================
// Root model + App section. Other Config.json sections live in
// sibling partial files for navigability:
//   AppConfigModel.Script.cs   -- Script / Detect / Install / Uninstall / Console
//   AppConfigModel.Intune.cs   -- IntunePackager + Intune package metadata
//   AppConfigModel.Sccm.cs     -- SCCMPackager + SCCM site / deployment metadata
//   AppConfigModel.Tenants.cs  -- Intune tenants, assignments, domain entries
//
// Partial classes are a compile-time construct; at runtime the model is one
// type per name. Source generators (CommunityToolkit.Mvvm) see the combined
// surface and emit the same code as the original single-file form.
// ============================================================

/// <summary>
/// C# mirror of Config.json. Uses ObservableCollection throughout for DataGrid/ListView binding.
/// SCCMSite, IntuneTenant, Domain use lists with a Key property (dict key stored on the entry)
/// so the UI can add/remove entries with a ListView + detail panel pattern.
/// </summary>
public partial class AppConfigModel : ObservableObject
{
    [ObservableProperty] private AppSection _app = new();
    [ObservableProperty] private ScriptSection _script = new();
    [ObservableProperty] private ObservableCollection<SCCMSiteEntry> _sccmSites = new();
    [ObservableProperty] private ObservableCollection<IntuneTenantEntry> _intuneTenants = new();
    [ObservableProperty] private ObservableCollection<DomainEntry> _domains = new();
}

// ============================================================
// App section -- top-level metadata: company, name, GUID, version,
// installer files, dependencies. Mirrors Config.App in Config.json.
// ============================================================

public partial class AppSection : ObservableObject
{
    /// <summary>Script framework: "Appease" (default) or "PSADT".</summary>
    [ObservableProperty] private string _scriptFramework = "Appease";

    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private string _company = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _language = string.Empty;
    [ObservableProperty] private string _eXEFile = string.Empty;
    [ObservableProperty] private string _mSIFile = string.Empty;
    [ObservableProperty] private string _gUID = string.Empty;
    [ObservableProperty] private string _uRL = string.Empty;
    [ObservableProperty] private string _dotVersion = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _iconFile = string.Empty;
    /// <summary>
    /// Icon provenance: true when the icon was a deliberate choice (browsed,
    /// library-rendered, image drop, or an explicit pick in the old-vs-new
    /// dialog); false for icons auto-extracted from an installer. Drives
    /// whether a Full installer apply asks before replacing the icon
    /// (IconPromptDecision) - auto icons are replaced silently, chosen icons
    /// prompt. Persisted so the protection survives save/reopen.
    /// </summary>
    [ObservableProperty] private bool _iconUserChosen;
    [ObservableProperty] private ObservableCollection<string> _dependencies = new();
    [ObservableProperty] private ObservableCollection<DetectRunningEntry> _detectRunning = new();
}

public partial class DetectRunningEntry : ObservableObject
{
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _exeFileName = string.Empty;
    [ObservableProperty] private string _process = string.Empty;
}
