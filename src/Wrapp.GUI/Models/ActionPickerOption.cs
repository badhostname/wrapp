using System.Windows.Media;

namespace Wrapp.Models;

/// <summary>
/// Describes a single selectable action card in an ActionPickerDialog.
/// </summary>
public class ActionPickerOption
{
    /// <summary>Machine-readable identifier returned via SelectedKey (e.g. "apply", "create").</summary>
    public required string Key { get; init; }

    /// <summary>Segoe MDL2 Assets glyph string (e.g. "\uE74E"). Ignored when IconImage is set.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Optional image icon (e.g. PNG resource). When set, takes precedence over the glyph Icon.</summary>
    public ImageSource? IconImage { get; init; }

    /// <summary>Bold title displayed on the card.</summary>
    public required string Title { get; init; }

    /// <summary>Secondary description text below the title.</summary>
    public required string Description { get; init; }

    /// <summary>When false, the card is dimmed and not clickable.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Tooltip shown when the card is disabled.</summary>
    public string? DisabledReason { get; init; }
}
