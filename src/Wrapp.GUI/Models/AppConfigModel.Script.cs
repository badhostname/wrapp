using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Helpers;

namespace Wrapp.Models;

// ============================================================
// Script section -- detection logic, install/uninstall script
// settings, and the console banner. Mirrors Config.Script in
// Config.json. Split out of AppConfigModel.cs for navigability.
// ============================================================

public partial class ScriptSection : ObservableObject
{
    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private DetectSection _detect = new();
    [ObservableProperty] private InstallScriptSection _install = new();
    [ObservableProperty] private UninstallScriptSection _uninstall = new();
    [ObservableProperty] private ConsoleSection _console = new();
    [ObservableProperty] private IntunePackagerSection _intunePackager = new();
    [ObservableProperty] private SCCMPackagerSection _sCCMPackager = new();
}

public partial class DetectSection : ObservableObject
{
    [ObservableProperty] private string _expression_Default = string.Empty;
    [ObservableProperty] private ObservableCollection<ExpressionEntry> _expressions = new();
    [ObservableProperty] private ObservableCollection<DetectionTest> _tests = new();
}

/// <summary>
/// A named detection expression beyond the default (e.g. Expression_Project, Expression_Visio).
/// Key is the suffix after "Expression_" and Expression is the boolean string.
/// </summary>
public partial class ExpressionEntry : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _expression = string.Empty;
}

public partial class DetectionTest : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _symbol = string.Empty;
    // Path or Command (mutually exclusive in DetectScript logic)
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private string _property = string.Empty;
    [ObservableProperty] private string _operator = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
    /// <summary>UI-only selection state (not serialized to Config.json).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;

    /// <summary>UI-only: true when Path was set via Browse (locks manual editing).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPathLocked;

    /// <summary>UI-only: property options for the current browsed path (file properties or registry values).</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private ObservableCollection<string> _availableProperties = new();

    /// <summary>UI-only: maps property names to their current values for auto-fill.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private Dictionary<string, string> _propertyValues = new();

    /// <summary>UI-only: true when this test's Symbol is duplicated by another test.</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSymbolDuplicate;

    // -- Path/Command mutual exclusivity (FieldStateProvider handles enable/disable;
    //    IsPathLockedByCommand stays hand-coded because the framework can't express
    //    "Command non-empty AND NOT IsPathLocked" -- used only for the small lock badge) --

    public static readonly FieldRule[] Rules = FieldDependencyRules.DetectionTestRules;

    private readonly FieldStateProvider _fieldStateProvider = new();

    /// <summary>XAML-binding accessor: <c>{Binding FieldStates[FieldName].IsEnabled}</c>.</summary>
    [JsonIgnore]
    public FieldStateAccessor FieldStates => _fieldStateProvider.FieldStates;

    public DetectionTest()
    {
        _fieldStateProvider.Bind(this, Rules);
    }

    /// <summary>True when Path text is locked because Command has content (browse buttons remain active).</summary>
    [JsonIgnore]
    public bool IsPathLockedByCommand => !string.IsNullOrWhiteSpace(Command) && !IsPathLocked;

    partial void OnCommandChanged(string value) => OnPropertyChanged(nameof(IsPathLockedByCommand));
    partial void OnIsPathLockedChanged(bool value) => OnPropertyChanged(nameof(IsPathLockedByCommand));
}

public partial class InstallScriptSection : ObservableObject
{
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private bool _uninstallFirst;
    [ObservableProperty] private bool _runAsAdmin = true;
    [ObservableProperty] private bool _detectApp;
    [ObservableProperty] private bool _closeRunning;
    [ObservableProperty] private string _backgroundColor = string.Empty;
    [ObservableProperty] private string _foregroundColor = string.Empty;
}

public partial class UninstallScriptSection : ObservableObject
{
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private bool _runAsAdmin = true;
    [ObservableProperty] private bool _detectApp;
    [ObservableProperty] private bool _closeRunning;
    [ObservableProperty] private string _backgroundColor = string.Empty;
    [ObservableProperty] private string _foregroundColor = string.Empty;
}

public partial class ConsoleSection : ObservableObject
{
    [ObservableProperty] private string _tag = string.Empty;
}
