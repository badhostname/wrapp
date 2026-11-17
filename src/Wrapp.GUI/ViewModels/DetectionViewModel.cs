using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using static Wrapp.Services.AppLogger;

namespace Wrapp.ViewModels;

public partial class DetectionViewModel : ObservableObject
{
    private readonly GeneralViewModel _appInfoVm;

    public static IReadOnlyList<string> ValidOperators { get; } = new[]
    {
        "-eq", "-ne", "-lt", "-le", "-gt", "-ge",
        "-like", "-notlike", "-match", "-notmatch",
        "-contains", "-notcontains"
    };

    // Note: file properties are now built dynamically per-file in
    // DetectionView.xaml.cs -> GetFilePropertyValues, so only the members
    // actually populated on the selected file appear in the dropdown.
    // See DetectScript.ps1 -- property names must match what
    // (Get-ItemProperty $path).$Property will resolve to at runtime.

    [ObservableProperty] private string _symbolWarning = string.Empty;
    [ObservableProperty] private bool _hasSymbolWarning;
    [ObservableProperty] private bool _hasAnySelected;

    /// <summary>
    /// Number of tests whose Symbol collides with another test's - relayed to
    /// <see cref="MainViewModel.DetectionErrorCount"/> for the Detection nav
    /// badge (same pipeline as the Intune/SCCM error counts). Kept in
    /// <see cref="Validate"/> so it can never drift from the per-row
    /// <see cref="DetectionTest.IsSymbolDuplicate"/> flags.
    /// </summary>
    [ObservableProperty] private int _errorCount;

    /// <summary>
    /// Non-blocking problems (see <see cref="DetectionRules"/>), one per
    /// problem - relayed to <see cref="MainViewModel.DetectionWarningCount"/>
    /// for the amber nav badge, as the Intune/SCCM warning counts are.
    /// </summary>
    [ObservableProperty] private int _warningCount;

    // Section-level required text beside the headers, like the Intune view's
    // "A tenant must be selected"; per-row problems beside the test buttons.
    [ObservableProperty] private bool _isDefaultExpressionMissing;
    [ObservableProperty] private string _expressionWarning = string.Empty;
    [ObservableProperty] private bool _hasExpressionWarning;
    [ObservableProperty] private bool _isTestTableMissing;
    [ObservableProperty] private string _symbolMissingWarning = string.Empty;
    [ObservableProperty] private bool _hasSymbolMissingWarning;
    [ObservableProperty] private string _symbolUnusedWarning = string.Empty;
    [ObservableProperty] private bool _hasSymbolUnusedWarning;

    /// <summary>How many unused symbols the warning names before "and N more".</summary>
    private const int UnusedSymbolsNamed = 6;

    private readonly ChildCollectionWatcher _testsWatcher;
    private readonly ChildCollectionWatcher _expressionsWatcher;
    private DetectSection? _wired;

    public DetectionViewModel(GeneralViewModel appInfoVm)
    {
        _appInfoVm = appInfoVm;
        _testsWatcher = new ChildCollectionWatcher(OnTestChanged);
        _expressionsWatcher = new ChildCollectionWatcher(OnExpressionChanged);
        _appInfoVm.ConfigLoaded += OnConfigLoaded;
        Wire();
    }

    private void OnConfigLoaded(object? sender, (AppConfigModel Config, string Path) e)
    {
        OnPropertyChanged(nameof(Detect));
        Wire();
    }

    public DetectSection Detect => _appInfoVm.FullConfig.Script.Detect;

    [RelayCommand]
    private void AddTest()
    {
        var usedSymbols = new HashSet<string>(Detect.Tests.Select(t => t.Symbol));
        var nextSymbol = "A";
        for (char c = 'A'; c <= 'Z'; c++)
        {
            if (!usedSymbols.Contains(c.ToString()))
            {
                nextSymbol = c.ToString();
                break;
            }
        }

        var test = new DetectionTest
        {
            Name     = nextSymbol,
            Symbol   = nextSymbol,
            Operator = "-eq",
            Value    = "True"
        };
        Detect.Tests.Add(test); // the watcher subscribes it and revalidates
        Info($"Detection: added test '{nextSymbol}'");
    }

    [RelayCommand]
    private void AddExpression()
    {
        Detect.Expressions.Add(new ExpressionEntry { Key = "", Expression = "" });
    }

    [RelayCommand]
    private void RemoveExpression(ExpressionEntry entry)
    {
        Detect.Expressions.Remove(entry);
    }

    [RelayCommand]
    private void RemoveSelectedTests()
    {
        var toRemove = Detect.Tests.Where(t => t.IsSelected).ToList();
        foreach (var t in toRemove)
            Detect.Tests.Remove(t);
        if (toRemove.Count > 0)
            Info($"Detection: removed {toRemove.Count} test(s)");
    }

    /// <summary>
    /// Follows the bundle's detect section: its default expression, and every
    /// test and named expression in it, added, removed or edited. A load
    /// replaces the section, so the previous one is let go.
    /// </summary>
    private void Wire()
    {
        var detect = Detect;
        if (!ReferenceEquals(_wired, detect))
        {
            if (_wired is not null) _wired.PropertyChanged -= OnDetectChanged;
            detect.PropertyChanged += OnDetectChanged;
            _wired = detect;
        }
        _testsWatcher.Attach(detect.Tests);
        _expressionsWatcher.Attach(detect.Expressions);
        RefreshHasAnySelected();
        Validate();
    }

    private void OnDetectChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DetectSection.Tests):
            case nameof(DetectSection.Expressions):
                Wire(); // the collection itself was replaced
                break;
            case nameof(DetectSection.Expression_Default):
                Validate();
                break;
        }
    }

    /// <summary>A test was added or removed (null), or one of its properties changed.</summary>
    private void OnTestChanged(string? propertyName)
    {
        if (propertyName is null or nameof(DetectionTest.IsSelected))
            RefreshHasAnySelected();
        if (propertyName is null or nameof(DetectionTest.Symbol))
            Validate();
    }

    /// <summary>A named expression was added or removed (null), or its text changed.</summary>
    private void OnExpressionChanged(string? propertyName)
    {
        if (propertyName is null or nameof(ExpressionEntry.Expression))
            Validate();
    }

    private void RefreshHasAnySelected()
    {
        HasAnySelected = Detect.Tests.Any(t => t.IsSelected);
    }

    /// <summary>
    /// Runs <see cref="DetectionRules.Check"/> and spreads the result over the
    /// rows (for highlighting), the header text and the two badge counts.
    /// Only Symbol, Expression and membership changes call it, so the flags
    /// it sets here never re-enter it.
    /// </summary>
    private void Validate()
    {
        var check = DetectionRules.Check(Detect);

        var duplicates = check.DuplicateSymbols.ToHashSet(StringComparer.Ordinal);
        foreach (var test in Detect.Tests)
        {
            test.IsSymbolDuplicate = !string.IsNullOrEmpty(test.Symbol) && duplicates.Contains(test.Symbol);
            test.IsSymbolMissing = check.SymbolMissing.Contains(test);
            test.IsSymbolUnused = check.SymbolUnused.Contains(test);
        }
        foreach (var entry in Detect.Expressions)
            entry.IsExpressionMissing = check.MissingExpressions.Contains(entry);

        ErrorCount = Detect.Tests.Count(t => t.IsSymbolDuplicate);
        HasSymbolWarning = duplicates.Count > 0;
        SymbolWarning = HasSymbolWarning
            ? $"Duplicate symbols: {string.Join(", ", check.DuplicateSymbols)}. Each test must have a unique symbol."
            : string.Empty;

        IsDefaultExpressionMissing = check.DefaultExpressionMissing;
        HasExpressionWarning = check.BlankExpressionCount > 0;
        ExpressionWarning = check.BlankExpressionCount switch
        {
            0 => string.Empty,
            1 => "An expression must be entered",
            var n => $"{n} expressions must be entered",
        };

        IsTestTableMissing = check.TestsMissing;

        HasSymbolMissingWarning = check.SymbolMissing.Count > 0;
        SymbolMissingWarning = check.SymbolMissing.Count switch
        {
            0 => string.Empty,
            1 => "A test needs a symbol",
            var n => $"{n} tests need a symbol",
        };

        var unused = check.UnusedSymbols;
        HasSymbolUnusedWarning = unused.Count > 0;
        SymbolUnusedWarning = unused.Count == 0 ? string.Empty
            : "Not used in any expression: " + string.Join(", ", unused.Take(UnusedSymbolsNamed))
              + (unused.Count > UnusedSymbolsNamed ? $" and {unused.Count - UnusedSymbolsNamed} more" : string.Empty);

        WarningCount = check.WarningCount;
    }
}
