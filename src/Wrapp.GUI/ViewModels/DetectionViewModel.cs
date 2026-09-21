using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// <see cref="ValidateSymbols"/> so it can never drift from the per-row
    /// <see cref="DetectionTest.IsSymbolDuplicate"/> flags.
    /// </summary>
    [ObservableProperty] private int _errorCount;

    public DetectionViewModel(GeneralViewModel appInfoVm)
    {
        _appInfoVm = appInfoVm;
        _appInfoVm.ConfigLoaded += OnConfigLoaded;
    }

    private void OnConfigLoaded(object? sender, (AppConfigModel Config, string Path) e)
    {
        OnPropertyChanged(nameof(Detect));
        WireTestEvents();
        ValidateSymbols();
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
        SubscribeTest(test);
        Detect.Tests.Add(test);
        ValidateSymbols();
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
        ValidateSymbols();
        RefreshHasAnySelected();
        if (toRemove.Count > 0)
            Info($"Detection: removed {toRemove.Count} test(s)");
    }

    private void WireTestEvents()
    {
        foreach (var t in Detect.Tests)
            SubscribeTest(t);
        Detect.Tests.CollectionChanged += OnTestsCollectionChanged;
    }

    private void OnTestsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (DetectionTest t in e.NewItems)
                SubscribeTest(t);
        RefreshHasAnySelected();
    }

    private void SubscribeTest(DetectionTest test)
    {
        test.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DetectionTest.Symbol))
                ValidateSymbols();
            if (args.PropertyName == nameof(DetectionTest.IsSelected))
                RefreshHasAnySelected();
        };
    }

    private void RefreshHasAnySelected()
    {
        HasAnySelected = Detect.Tests.Any(t => t.IsSelected);
    }

    private void ValidateSymbols()
    {
        var symbols = Detect.Tests.Select(t => t.Symbol).Where(s => !string.IsNullOrEmpty(s)).ToList();
        var duplicateKeys = symbols.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        // Mark individual tests so the UI can highlight their Symbol cells
        foreach (var test in Detect.Tests)
            test.IsSymbolDuplicate = !string.IsNullOrEmpty(test.Symbol) && duplicateKeys.Contains(test.Symbol);

        ErrorCount = Detect.Tests.Count(t => t.IsSymbolDuplicate);

        if (duplicateKeys.Count > 0)
        {
            SymbolWarning = $"Duplicate symbols: {string.Join(", ", duplicateKeys)}. Each test must have a unique symbol.";
            HasSymbolWarning = true;
        }
        else
        {
            SymbolWarning = string.Empty;
            HasSymbolWarning = false;
        }
    }
}
