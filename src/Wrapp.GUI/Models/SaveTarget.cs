using System.IO;
using System.Text.Json;
using Wrapp.Services;

namespace Wrapp.Models;

/// <summary>How the save sheet turns a chosen location into the bundle folder.</summary>
public enum SaveTargetMode
{
    /// <summary>Create the <see cref="AppSettings.DirectoryFormat"/> folders under the location.</summary>
    Convention,
    /// <summary>Save into the location itself; the operator made the folder.</summary>
    Direct,
}

/// <summary>
/// The folder a save lands in, as the save sheet edits it: a location, a
/// mode, and in convention mode the relative path the naming convention
/// suggests (editable for this save only). Pure apart from the probes that
/// produce warnings; the sheet renders it and the pipeline saves to
/// <see cref="ResolvedPath"/>. Nothing here writes a path anywhere.
/// </summary>
public sealed class SaveTarget
{
    private readonly List<string> _warnings = new();
    private readonly List<string> _missingFields = new();

    /// <summary>The browsed folder.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Where <see cref="Location"/> came from, for the hint under the box.</summary>
    public string LocationSource { get; set; } = string.Empty;

    public SaveTargetMode Mode { get; set; } = SaveTargetMode.Convention;

    /// <summary>Convention mode: the folders to create under the location.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The convention's own suggestion, so the sheet can say "from {format}" and the operator can see an edit.</summary>
    public string SuggestedRelativePath { get; private set; } = string.Empty;

    /// <summary>Why this target cannot be saved to, or null when it can.</summary>
    public string? Error { get; private set; }

    /// <summary>Things worth knowing before saving here; never blockers.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Convention mode: the app fields the format uses that are still empty.</summary>
    public IReadOnlyList<string> MissingFields => _missingFields;

    /// <summary>The folder the bundle is saved to.</summary>
    public string ResolvedPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Location)) return string.Empty;
            var location = Location.Trim();
            if (Mode == SaveTargetMode.Direct || string.IsNullOrWhiteSpace(RelativePath)) return location;
            try { return Path.Combine(location, RelativePath.Trim().Replace('/', '\\')); }
            catch (ArgumentException) { return location; }
        }
    }

    public static SaveTargetMode ModeFrom(string? setting)
        => string.Equals(setting, BundleService.SaveModeDirect, StringComparison.OrdinalIgnoreCase)
            ? SaveTargetMode.Direct
            : SaveTargetMode.Convention;

    public static string ModeSetting(SaveTargetMode mode)
        => mode == SaveTargetMode.Direct ? BundleService.SaveModeDirect : BundleService.SaveModeConvention;

    /// <summary>
    /// A target for the sheet: the mode from Settings, the location by
    /// precedence (last used on this computer, the organisation's default,
    /// the current bundle's parent, Documents), and the convention's suggestion.
    /// </summary>
    public static SaveTarget For(AppSettings settings, AppSection app, string? currentBundleRoot)
    {
        var (location, source) = StartingLocation(settings, currentBundleRoot);
        var target = new SaveTarget
        {
            Location = location,
            LocationSource = source,
            Mode = ModeFrom(settings.BundleSaveMode),
        };
        target.SuggestRelativePath(settings, app);
        target.Refresh(settings, app, currentBundleRoot);
        return target;
    }

    /// <summary>Where the sheet opens, and why.</summary>
    public static (string Location, string Source) StartingLocation(AppSettings settings, string? currentBundleRoot)
    {
        if (!string.IsNullOrWhiteSpace(settings.LastBundleOutputRoot) && Directory.Exists(settings.LastBundleOutputRoot))
            return (settings.LastBundleOutputRoot, "last used on this computer");
        if (!string.IsNullOrWhiteSpace(settings.BundleOutputRoot))
            return (settings.BundleOutputRoot, "the organisation's default location, set in Settings");
        if (!string.IsNullOrWhiteSpace(currentBundleRoot)
            && !IsUnder(currentBundleRoot, TempWorkspaceService.RootPath)
            && Path.GetDirectoryName(currentBundleRoot.TrimEnd('\\', '/')) is { Length: > 0 } parent)
            return (parent, "the folder the current bundle is in");
        return (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "your Documents folder");
    }

    /// <summary>Resets <see cref="RelativePath"/> to what the naming convention produces for <paramref name="app"/>.</summary>
    public void SuggestRelativePath(AppSettings settings, AppSection app)
    {
        SuggestedRelativePath = BundleService.TryResolveSubDirectory(settings, app, out var sub, out _)
            ? sub
            : UnvalidatedSuggestion(settings, app);
        RelativePath = SuggestedRelativePath;
    }

    // The format with the tokens filled in, even when a value is empty: the
    // sheet shows "Ricoh\\4_45_0_0" with the hole visible rather than nothing.
    private static string UnvalidatedSuggestion(AppSettings settings, AppSection app)
        => BundleService.EffectiveDirectoryFormat(settings)
            .Replace("{Company}",    BundleService.FolderName(settings, app.Company))
            .Replace("{Name}",       BundleService.FolderName(settings, app.Name))
            .Replace("{Version}",    BundleService.FolderName(settings, app.Version))
            .Replace("{DotVersion}", BundleService.FolderName(settings, app.DotVersion))
            .Replace("{Language}",   BundleService.FolderName(settings, app.Language));

    /// <summary>Recomputes the error, the missing fields and the warnings for the current values.</summary>
    public void Refresh(AppSettings settings, AppSection app, string? currentBundleRoot)
    {
        _warnings.Clear();
        _missingFields.Clear();
        Error = null;

        if (string.IsNullOrWhiteSpace(Location))
        {
            Error = "Choose a location.";
            return;
        }
        if (!Path.IsPathRooted(Location.Trim()) || Location.Trim().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            Error = $"'{Location}' is not a full folder path.";
            return;
        }

        if (Mode == SaveTargetMode.Convention)
        {
            var fmt = BundleService.EffectiveDirectoryFormat(settings);
            if (fmt.Contains("{Company}")    && string.IsNullOrWhiteSpace(app.Company))    _missingFields.Add("Company");
            if (fmt.Contains("{Name}")       && string.IsNullOrWhiteSpace(app.Name))       _missingFields.Add("App Name");
            if (fmt.Contains("{Version}")    && string.IsNullOrWhiteSpace(app.Version))    _missingFields.Add("Version (e.g. 1_2_3)");
            if (fmt.Contains("{DotVersion}") && string.IsNullOrWhiteSpace(app.DotVersion)) _missingFields.Add("Dot Version (e.g. 1.2.3)");
            if (fmt.Contains("{Language}")   && string.IsNullOrWhiteSpace(app.Language))   _missingFields.Add("Language");

            if (_missingFields.Count > 0 && string.Equals(RelativePath.Trim(), SuggestedRelativePath, StringComparison.Ordinal))
            {
                // The suggestion has a hole in it; say which field, not what the hole did to the path.
                // An edited path is the operator's own and is validated like any other.
                Error = $"Fill in {string.Join(", ", _missingFields)} first, edit the folders above, or save directly into the location.";
                return;
            }
            if (string.IsNullOrWhiteSpace(RelativePath))
            {
                Error = "Enter the folders to create, or save directly into the location.";
                return;
            }
            if (BundleService.RelativeFolderPathError(RelativePath.Trim().Replace('/', '\\'), "The folders to create") is { } relError)
            {
                Error = relError;
                return;
            }
        }

        var resolved = ResolvedPath;
        ProbeWarnings(resolved, currentBundleRoot);
    }

    private void ProbeWarnings(string resolved, string? currentBundleRoot)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved)); }
        catch (Exception) { return; }

        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            _warnings.Add("This is the root of a drive or share.");

        if (IsUnder(full, TempWorkspaceService.RootPath))
            _warnings.Add("This is Wrapp's temporary workspace; it is cleaned up automatically.");

        if (!string.IsNullOrEmpty(currentBundleRoot)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentBundleRoot)), full, StringComparison.OrdinalIgnoreCase))
            _warnings.Add("This is the current bundle's own folder; saving here is a save in place.");
        else if (BundleService.FindConfigJson(full) is { } existing)
            _warnings.Add($"This folder already holds a bundle{DescribeBundle(existing)}. Saving here replaces its Config.json, scripts and shortcuts.");
        else if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            _warnings.Add("This folder is not empty.");

        var parent = Path.GetDirectoryName(full);
        if (parent is not null && BundleService.FindBundleRoot(parent) is { } outer)
            _warnings.Add($"This folder is inside another bundle ({outer}). The next packaging run of that bundle would include it.");
    }

    /// <summary><paramref name="path"/> is <paramref name="root"/> or inside it; a sibling that merely shares the prefix (<c>Wrapp</c> vs <c>WrappTests</c>) is not.</summary>
    private static bool IsUnder(string path, string root)
    {
        try
        {
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // " (Universal Print Driver PCL6 4.44.0.0)" when the config can be read, else "".
    private static string DescribeBundle(string configPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!doc.RootElement.TryGetProperty("App", out var app)) return string.Empty;
            var name = app.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var version = app.TryGetProperty("DotVersion", out var v) ? v.GetString() : null;
            var text = $"{name} {version}".Trim();
            return text.Length == 0 ? string.Empty : $" ({text})";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
