using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color;                    // WinForms also declares these
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Wrapp.Services;

/// <summary>
/// A parsed theme file (any .json): a named, sparse color overlay onto one of
/// the two compiled base themes. JSON colors only - never XAML (loading
/// arbitrary XAML is a code-execution vector). The file name and extension
/// don't matter; the content does: it must have exactly the template's shape
/// (<see cref="Properties"/>), and unknown properties, unknown keys and
/// unparsable colors are rejected BY NAME so an org theme fails loudly, not
/// half-applied.
/// </summary>
public sealed class WrappThemeFile
{
    /// <summary>Every property a theme file may have - the template. "$schema" is also allowed (editors use it).</summary>
    public static readonly IReadOnlyList<string> Properties = new[] { "Name", "BaseTheme", "MonacoTheme", "Colors", "ShadowOpacity" };

    /// <summary>The code editors' two themes.</summary>
    public static readonly IReadOnlyList<string> MonacoThemes = new[] { "vs", "vs-dark" };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Name { get; set; } = string.Empty;

    /// <summary>"Dark" or "Light" - the compiled dictionary used as the base
    /// layer and the Wpf.Ui ApplicationTheme hint.</summary>
    public string BaseTheme { get; set; } = "Dark";

    /// <summary>Monaco editor theme; defaults from BaseTheme when omitted.</summary>
    public string? MonacoTheme { get; set; }

    /// <summary>Theme key → hex color. Any subset of the base theme's keys.</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional PopupShadow opacity override (0..1).</summary>
    public double? ShadowOpacity { get; set; }

    [JsonIgnore] public string? FilePath { get; set; }

    /// <summary>
    /// Validation against the template (no WPF needed - unit-testable): only
    /// the template's properties, name present, BaseTheme and MonacoTheme
    /// valid, Colors a map of color strings, every color parsable, shadow in
    /// range. Key-existence against the base dictionary happens at
    /// apply/import time.
    /// </summary>
    public static WrappThemeFile? TryParse(string json, out string? error)
    {
        error = null;
        if (ShapeError(json) is { } shape)
        {
            error = shape;
            return null;
        }
        WrappThemeFile? theme;
        try
        {
            theme = JsonSerializer.Deserialize<WrappThemeFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            error = $"Not valid JSON: {ex.Message}";
            return null;
        }
        if (theme is null) { error = "Empty theme file."; return null; }

        if (string.IsNullOrWhiteSpace(theme.Name))
        { error = "Theme file has no \"Name\"."; return null; }
        if (theme.BaseTheme is not ("Dark" or "Light"))
        { error = $"BaseTheme must be \"Dark\" or \"Light\" (got \"{theme.BaseTheme}\")."; return null; }
        if (theme.MonacoTheme is { } monaco && !MonacoThemes.Contains(monaco))
        { error = $"MonacoTheme must be \"vs\" (light) or \"vs-dark\" (got \"{monaco}\")."; return null; }
        if (theme.ShadowOpacity is < 0 or > 1)
        { error = "ShadowOpacity must be between 0 and 1."; return null; }

        foreach (var (key, value) in theme.Colors)
        {
            try { ColorConverter.ConvertFromString(value); }
            catch
            {
                error = $"\"{key}\" has an unparsable color value \"{value}\".";
                return null;
            }
        }
        return theme;
    }

    /// <summary>
    /// What is wrong with the file's shape, or null: a JSON object with only
    /// the template's properties, Colors an object of strings. Any .json can
    /// be offered as a theme, so this is what tells a theme from other JSON -
    /// and a misspelt property ("Colours") from a theme that changes nothing.
    /// </summary>
    private static string? ShapeError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "A theme file is a JSON object (see the theme template).";
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name == "$schema") continue;
                if (!Properties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    return $"Unknown property \"{property.Name}\" - a theme file has only {string.Join(", ", Properties.Take(Properties.Count - 1))} and {Properties[^1]}.";
                if (string.Equals(property.Name, "Colors", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                        return "\"Colors\" must be an object of theme key -> color (\"AccentBrush\": \"#2D6BC4\").";
                    foreach (var color in property.Value.EnumerateObject())
                        if (color.Value.ValueKind != JsonValueKind.String)
                            return $"\"{color.Name}\" must be a color string such as \"#2D6BC4\".";
                }
            }
            return null;
        }
        catch (JsonException ex)
        {
            return $"Not valid JSON: {ex.Message}";
        }
    }

    /// <summary>
    /// The theme template: every property, and every color with its value in
    /// the theme it starts from - a complete file to rename, edit and import.
    /// Pure (the caller reads the colors), so a test holds it to
    /// <see cref="TryParse"/>.
    /// </summary>
    public static string Template(string name, string baseTheme, string monacoTheme,
        IEnumerable<KeyValuePair<string, Color>> colors, double? shadowOpacity)
    {
        var template = new WrappThemeFile
        {
            Name = name,
            BaseTheme = baseTheme,
            MonacoTheme = monacoTheme,
            ShadowOpacity = shadowOpacity,
        };
        foreach (var (key, color) in colors.OrderBy(c => c.Key, StringComparer.Ordinal))
            template.Colors[key] = Hex(color);
        return JsonSerializer.Serialize(template, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
    }

    /// <summary>"#RRGGBB", or "#AARRGGBB" when the color is not opaque.</summary>
    internal static string Hex(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>One selectable theme: the two built-ins or a custom file.</summary>
public sealed record ThemeChoice(string Name, bool IsBuiltIn, string? FilePath);

/// <summary>
/// The theme engine (extracted from App.ApplyTheme): discovery, validation,
/// dictionary build (base + overlay), live apply with the accent read FROM
/// the dictionary (replacing the former hardcoded accent ternary), and the
/// Preview/EndPreview seams the future Theme Studio drives.
/// </summary>
public static class ThemeService
{
    /// <summary>Theme files are plain .json; what makes one a theme is its content (<see cref="WrappThemeFile.TryParse"/>).</summary>
    public const string FileExtension = ".json";

    /// <summary>The open/save dialogs' filter.</summary>
    public const string FileFilter = "Theme files (*.json)|*.json|All files|*.*";

    public static string ThemesDir => Path.Combine(PlatformConfig.WrappRoot, "Themes");

    // -------------------------------------------------------------------
    // Discovery
    // -------------------------------------------------------------------

    /// <summary>Dark, Light, then valid custom themes (every .json in the
    /// user dir that passes the template's validation + the ThemeFilePath
    /// policy file). Other JSON is skipped with a log line.</summary>
    public static List<ThemeChoice> Available()
    {
        var list = new List<ThemeChoice>
        {
            new("Dark", IsBuiltIn: true, null),
            new("Light", IsBuiltIn: true, null),
        };

        var paths = new List<string>();
        try
        {
            if (Directory.Exists(ThemesDir))
                paths.AddRange(Directory.EnumerateFiles(ThemesDir, "*" + FileExtension));
        }
        catch { /* unreadable dir - built-ins only */ }

        if (Policy.PolicyService.Current.ThemeFilePath is { Length: > 0 } policyPath
            && File.Exists(policyPath))
            paths.Add(policyPath);

        foreach (var path in paths)
        {
            var theme = TryLoadFile(path, out var error);
            if (theme is null)
            {
                AppLogger.Warn($"Theme: skipping '{Path.GetFileName(path)}' - {error}");
                continue;
            }
            if (list.Any(c => string.Equals(c.Name, theme.Name, StringComparison.OrdinalIgnoreCase)))
                continue; // first wins (user dir before policy path duplicates)
            list.Add(new ThemeChoice(theme.Name, IsBuiltIn: false, path));
        }
        return list;
    }

    public static WrappThemeFile? TryLoadFile(string path, out string? error)
    {
        try
        {
            var theme = WrappThemeFile.TryParse(File.ReadAllText(path), out error);
            if (theme is not null) theme.FilePath = path;
            return theme;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Validates + copies a theme file into the user Themes dir.
    /// Also rejects colors whose key does not exist in the base dictionary.
    /// One file per theme name: an earlier copy under another file name (a
    /// .wrapptheme.json from before 1.0.14, a renamed file) is replaced, not
    /// left to shadow the new one.</summary>
    public static ThemeChoice Import(string sourcePath)
    {
        var theme = TryLoadFile(sourcePath, out var error)
            ?? throw new InvalidOperationException($"Not a valid Wrapp theme: {error}");

        var unknown = UnknownKeys(theme);
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"Theme \"{theme.Name}\" uses unknown color key(s): {string.Join(", ", unknown)}");

        Directory.CreateDirectory(ThemesDir);
        var source = Path.GetFullPath(sourcePath);
        var inThemesDir = string.Equals(Path.GetDirectoryName(source), Path.GetFullPath(ThemesDir), StringComparison.OrdinalIgnoreCase);
        var dest = inThemesDir ? source : Path.Combine(ThemesDir,
            TemplateService.SanitizeTemplateFileName(theme.Name) + FileExtension);

        foreach (var other in Directory.EnumerateFiles(ThemesDir, "*" + FileExtension).ToList())
        {
            if (string.Equals(Path.GetFullPath(other), dest, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryLoadFile(other, out _) is { } earlier && string.Equals(earlier.Name, theme.Name, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(other);
                AppLogger.Info($"Theme: replaced the earlier copy of \"{theme.Name}\" ({Path.GetFileName(other)})");
            }
        }
        if (!inThemesDir) File.Copy(source, dest, overwrite: true);
        AppLogger.Info($"Theme: imported \"{theme.Name}\" -> {dest}");
        return new ThemeChoice(theme.Name, IsBuiltIn: false, dest);
    }

    /// <summary>
    /// The theme template, started from <paramref name="themeName"/> (a
    /// built-in or a custom theme): its base, its editor theme, every color
    /// as it looks now, under a new name. Save it, change what you like,
    /// import it.
    /// </summary>
    public static string CreateTemplate(string? themeName, string newName = "My theme")
    {
        var custom = themeName is "Dark" or "Light" or null ? null
            : Available().FirstOrDefault(c => !c.IsBuiltIn && string.Equals(c.Name, themeName, StringComparison.OrdinalIgnoreCase)) is { FilePath: { } fp }
                ? TryLoadFile(fp, out _) : null;
        var baseName = custom?.BaseTheme ?? (themeName == "Light" ? "Light" : "Dark");
        var dict = BuildDictionary(baseName, custom);

        var colors = new List<KeyValuePair<string, Color>>();
        foreach (var key in dict.Keys.OfType<string>())
        {
            if (key == MarkerKey) continue;
            if (dict[key] is SolidColorBrush brush) colors.Add(new(key, brush.Color));
            else if (dict[key] is Color color) colors.Add(new(key, color));
        }
        var shadow = dict["PopupShadow"] is System.Windows.Media.Effects.DropShadowEffect s ? Math.Round(s.Opacity, 2) : (double?)null;
        var monaco = custom?.MonacoTheme is { Length: > 0 } m ? m : baseName == "Light" ? "vs" : "vs-dark";
        return WrappThemeFile.Template(newName, baseName, monaco, colors, shadow);
    }

    /// <summary>Color keys in the file that don't exist in its base dictionary.</summary>
    public static List<string> UnknownKeys(WrappThemeFile theme)
    {
        var baseDict = LoadBaseDictionary(theme.BaseTheme);
        return theme.Colors.Keys
            .Where(k => !baseDict.Contains(k) || baseDict[k] is not (SolidColorBrush or Color))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // -------------------------------------------------------------------
    // Apply
    // -------------------------------------------------------------------

    /// <summary>
    /// Applies a theme by name (built-in or custom). Unknown names fall back
    /// to Dark with a log line (previously a silent ternary). Returns the
    /// effective name.
    /// </summary>
    public static string Apply(string? themeName)
    {
        var name = themeName ?? "Dark";
        if (name is "Dark" or "Light")
        {
            ApplyCore(name, overlay: null);
            return name;
        }

        var choice = Available().FirstOrDefault(
            c => !c.IsBuiltIn && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        var theme = choice?.FilePath is { } fp ? TryLoadFile(fp, out _) : null;
        if (theme is null)
        {
            AppLogger.Warn($"Theme: '{name}' not found - falling back to Dark");
            ApplyCore("Dark", overlay: null);
            return "Dark";
        }
        ApplyCore(theme.BaseTheme, theme);
        return name;
    }

    /// <summary>Theme Studio seam: applies an unsaved theme live (nothing
    /// persisted). Pair with <see cref="EndPreview"/> to restore.</summary>
    public static void Preview(WrappThemeFile theme) => ApplyCore(theme.BaseTheme, theme);

    /// <summary>Re-applies the persisted theme after a preview session.</summary>
    public static void EndPreview(string? persistedThemeName) => Apply(persistedThemeName);

    private static void ApplyCore(string baseName, WrappThemeFile? overlay)
    {
        var wpfTheme = baseName == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(wpfTheme);

        var dict = BuildDictionary(baseName, overlay);

        // Accent is read FROM the dictionary - custom themes steer every
        // Wpf.Ui control through their own AccentBrush; the built-ins keep
        // their exact former values (#9ac9cf dark / #366372 light).
        if (dict["AccentBrush"] is SolidColorBrush accentBrush)
            ApplicationAccentColorManager.Apply(accentBrush.Color, wpfTheme, false);

        var dicts = Application.Current.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("/Themes/") == true || d.Contains(MarkerKey));
        if (existing != null) dicts.Remove(existing);
        dicts.Add(dict);

        foreach (Window w in Application.Current.Windows)
        {
            if (w is Wpf.Ui.Controls.FluentWindow fw)
                fw.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
        }

        var monaco = overlay?.MonacoTheme is { Length: > 0 } m
            ? m
            : baseName == "Light" ? "vs" : "vs-dark";
        App.NotifyThemeApplied(monaco);
    }

    private const string MarkerKey = "__WrappCustomTheme";

    /// <summary>Base compiled dictionary + sparse brush/color overlay.</summary>
    internal static ResourceDictionary BuildDictionary(string baseName, WrappThemeFile? overlay)
    {
        var baseDict = LoadBaseDictionary(baseName);
        if (overlay is null) return baseDict;

        // Copy so the compiled dictionary instance is never mutated.
        var dict = new ResourceDictionary();
        foreach (var key in baseDict.Keys)
            dict[key] = baseDict[key];
        dict[MarkerKey] = overlay.Name;

        foreach (var (key, hex) in overlay.Colors)
        {
            if (!dict.Contains(key)) continue; // Import() rejects these; apply is lenient for hand-copied files
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            if (dict[key] is SolidColorBrush)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dict[key] = brush;
            }
            else if (dict[key] is Color)
            {
                dict[key] = color;
            }
        }

        if (overlay.ShadowOpacity is { } opacity
            && dict["PopupShadow"] is System.Windows.Media.Effects.DropShadowEffect shadow)
        {
            var clone = shadow.Clone();
            clone.Opacity = opacity;
            clone.Freeze();
            dict["PopupShadow"] = clone;
        }

        return dict;
    }

    private static ResourceDictionary LoadBaseDictionary(string baseName)
    {
        var name = baseName == "Light" ? "Light" : "Dark";
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Wrapp;component/Themes/{name}.xaml", UriKind.Absolute),
        };
    }
}
