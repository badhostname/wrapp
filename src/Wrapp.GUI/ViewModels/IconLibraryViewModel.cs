using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using Wrapp.Services;

namespace Wrapp.ViewModels;

/// <summary>
/// Backs <see cref="Views.IconLibraryDialog"/>: search over the Material
/// Design glyph catalogue (~7,000 names), paged 50 at a time so the dialog
/// opens instantly, plus tile-color selection and a live preview. Pure
/// filter/paging state - rendering stays in <see cref="IconTileRenderer"/> -
/// so the logic is unit-testable without a dispatcher.
/// </summary>
public partial class IconLibraryViewModel : ObservableObject
{
    /// <summary>
    /// Page size (5-wide grid rows). Starts at a full 50 so the results area
    /// is scroll-bound from the first paint - the pop-up never resizes when
    /// more rows load; Show more (at the BOTTOM of the scroll area) appends.
    /// </summary>
    public const int PageSize = 50;

    /// <summary>
    /// Distinct glyph names. PackIconKind has alias members (same value, many
    /// names); grouping by value keeps one canonical entry per glyph.
    /// </summary>
    private static readonly (string Name, PackIconKind Kind)[] Catalogue =
        Enum.GetValues<PackIconKind>()
            .GroupBy(k => (int)k)
            .Select(g => g.First())
            .Select(k => (k.ToString(), k))
            .OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public ObservableCollection<IconLibraryEntry> Results { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultSummary))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private IconLibraryEntry? _selected;

    public bool HasSelection => Selected is not null;

    // ------------------------------------------------------------------
    // Colors: one full picker per target. Background defaults to the app
    // accent; the glyph defaults to white (the classic tile look). Each
    // picker owns its own spectrum/hex/RGB state (ColorPickerViewModel).
    // ------------------------------------------------------------------

    private static readonly string[] GlyphPalette =
        new[] { "#FFFFFF", "#1E1E1E" }.Concat(IconTileRenderer.Palette).ToArray();

    /// <summary>Tile background color picker.</summary>
    public ColorPickerViewModel Background { get; } =
        new(IconTileRenderer.Palette[0], IconTileRenderer.Palette);

    /// <summary>Glyph (icon) color picker.</summary>
    public ColorPickerViewModel Glyph { get; } = new("#FFFFFF", GlyphPalette);

    /// <summary>Resolved tile background hex (what the renderer consumes).</summary>
    public string SelectedColor => Background.SelectedColor;

    /// <summary>Resolved glyph hex (what the renderer consumes).</summary>
    public string GlyphColor => Glyph.SelectedColor;

    private int _shown;
    private (string Name, PackIconKind Kind)[] _filtered = Catalogue;

    public IconLibraryViewModel() => Refill();

    public bool CanShowMore => _shown < _filtered.Length;

    public string ResultSummary =>
        _filtered.Length == 0
            ? "No icons match"
            : $"Showing {Results.Count} of {_filtered.Length:N0} icons";

    partial void OnSearchTextChanged(string value) => Refill();

    [RelayCommand]
    private void ShowMore() => AppendPage();

    private void Refill()
    {
        var term = SearchText.Trim();
        _filtered = term.Length == 0
            ? Catalogue
            : Catalogue.Where(e => e.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();

        Results.Clear();
        _shown = 0;
        Selected = null;
        AppendPage();
    }

    private void AppendPage()
    {
        foreach (var (name, kind) in _filtered.Skip(_shown).Take(PageSize))
            Results.Add(new IconLibraryEntry(name, kind));
        _shown = Math.Min(_shown + PageSize, _filtered.Length);
        OnPropertyChanged(nameof(CanShowMore));
        OnPropertyChanged(nameof(ResultSummary));
        ShowMoreCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One catalogue row; glyph path data resolves lazily per page.</summary>
public sealed class IconLibraryEntry
{
    public IconLibraryEntry(string name, PackIconKind kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }
    public PackIconKind Kind { get; }

    private string? _data;
    public string Data => _data ??= IconTileRenderer.GetGlyphData(Kind);
}
