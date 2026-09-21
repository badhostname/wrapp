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
/// A parsed .wrapptheme.json: a named, sparse color overlay onto one of the
/// two compiled base themes. JSON colors only - never XAML (loading arbitrary
/// XAML is a code-execution vector). Unknown keys and unparsable colors are
/// rejected BY NAME so an org theme fails loudly, not half-applied.
/// </summary>
public sealed class WrappThemeFile
{
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
    /// Schema-level validation (no WPF needed - unit-testable): name present,
    /// BaseTheme valid, every color parsable, shadow in range. Key-existence
    /// against the base dictionary happens at apply/import time.
    /// </summary>
    public static WrappThemeFile? TryParse(string json, out string? error)
    {
        error = null;
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
    public const string FileExtension = ".wrapptheme.json";

    public static string ThemesDir => Path.Combine(PlatformConfig.WrappRoot, "Themes");

    // -------------------------------------------------------------------
    // Discovery
    // -------------------------------------------------------------------

    /// <summary>Dark, Light, then valid custom themes (user dir + the
    /// ThemeFilePath policy file), sorted by name.</summary>
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
    /// Also rejects colors whose key does not exist in the base dictionary.</summary>
    public static ThemeChoice Import(string sourcePath)
    {
        var theme = TryLoadFile(sourcePath, out var error)
            ?? throw new InvalidOperationException($"Not a valid Wrapp theme: {error}");

        var unknown = UnknownKeys(theme);
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"Theme \"{theme.Name}\" uses unknown color key(s): {string.Join(", ", unknown)}");

        Directory.CreateDirectory(ThemesDir);
        var dest = Path.Combine(ThemesDir,
            TemplateService.SanitizeTemplateFileName(theme.Name) + FileExtension);
        File.Copy(sourcePath, dest, overwrite: true);
        AppLogger.Info($"Theme: imported \"{theme.Name}\" -> {dest}");
        return new ThemeChoice(theme.Name, IsBuiltIn: false, dest);
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
