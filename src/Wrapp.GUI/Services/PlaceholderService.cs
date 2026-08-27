using System.Linq;
using System.Text.RegularExpressions;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Workstream P: the single <c>{{Name}}</c> placeholder resolver. Replaces the
/// two formerly-duplicated ApplyTokens implementations (TemplateService /
/// BundleService) and adds user/org-defined CUSTOM placeholders on top of the
/// twelve built-ins.
/// <para>
/// Rules: built-ins always replace (even when empty — existing template
/// semantics depend on it). Custom placeholders replace when they have a
/// value, stay literal when known-but-empty (a visible TODO), and unknown
/// names are left untouched. Expansion is single-pass and non-recursive:
/// a VALUE containing <c>{{X}}</c> is emitted verbatim.
/// </para>
/// </summary>
public static class PlaceholderService
{
    /// <summary>
    /// Reserved built-in names (case-insensitive). Custom placeholders can
    /// never shadow these — an org file cannot hijack {{Name}}.
    /// </summary>
    public static readonly string[] BuiltInNames =
    {
        "Company", "Name", "Version", "DotVersion", "Language",
        "EXEFile", "MSIFile", "GUID", "Date", "Author",
        "TagFolder", "LocalAppFolder",
    };

    private static readonly Regex TokenRx = new(
        @"\{\{(?<name>[A-Za-z0-9_-]+)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ValidNameRx = new(
        @"^[A-Za-z0-9_-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Source of custom placeholders: (Name, resolved Value or null, IsSensitive).
    /// Wired at startup to the live settings + secure sidecar; tests swap it.
    /// Default: none.
    /// </summary>
    public static Func<IEnumerable<(string Name, string? Value, bool IsSensitive)>> CustomsSource
    { get; set; } = () => Enumerable.Empty<(string, string?, bool)>();

    /// <summary>
    /// Wires BOTH the custom-placeholder resolver and the sensitive-value log
    /// redaction from <paramref name="settings"/>. One method on purpose: the
    /// two used to be wired independently in two places, and any future
    /// mutation path that remembered the resolver but forgot the redaction
    /// would leak secret values into app.log. Call after any placeholder
    /// persistence change.
    /// </summary>
    public static void RefreshFromSettings(AppSettings settings)
    {
        CustomsSource = () => settings.Placeholders
            .Select(p => (p.Name,
                          p.IsSensitive ? PlaceholderSecureStore.GetValue(p.Name) : p.Value,
                          p.IsSensitive));
        AppLogger.SetSensitiveValuePatterns(settings.Placeholders
            .Where(p => p.IsSensitive)
            .Select(p => PlaceholderSecureStore.GetValue(p.Name) ?? string.Empty));
    }

    public static bool IsReservedName(string name)
        => BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Valid, non-reserved custom name.</summary>
    public static bool IsValidCustomName(string name)
        => !string.IsNullOrWhiteSpace(name) && ValidNameRx.IsMatch(name) && !IsReservedName(name);

    /// <summary>Expands all placeholders in <paramref name="content"/> (report discarded).</summary>
    public static string Expand(string content, AppSection app)
        => Expand(content, app, out _);

    /// <summary>
    /// Expands all placeholders and reports what happened — the report drives
    /// the replace-in-place summary dialogs (P3) and sensitive-value warnings.
    /// </summary>
    public static string Expand(string content, AppSection app, out PlaceholderExpandReport report)
    {
        var r = new PlaceholderExpandReport();
        report = r;
        if (string.IsNullOrEmpty(content)) return content ?? string.Empty;

        var customs = CustomsSource()
            .Where(c => IsValidCustomName(c.Name))
            .ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

        var result = TokenRx.Replace(content, m =>
        {
            var name = m.Groups["name"].Value;

            if (TryResolveBuiltIn(name, app, out var builtIn))
            {
                r.Replaced++;
                r.Track(name, builtIn, isSensitive: false, PlaceholderTokenOutcome.Replaced);
                return builtIn;
            }

            if (customs.TryGetValue(name, out var custom))
            {
                if (string.IsNullOrEmpty(custom.Value))
                {
                    r.LeftEmpty.Add(custom.Name);
                    r.Track(custom.Name, string.Empty, custom.IsSensitive, PlaceholderTokenOutcome.LeftEmpty);
                    return m.Value; // known but no value: visible TODO stays
                }
                r.Replaced++;
                if (custom.IsSensitive) r.SensitiveReplaced.Add(custom.Name);
                r.Track(custom.Name, custom.Value, custom.IsSensitive, PlaceholderTokenOutcome.Replaced);
                return custom.Value;
            }

            r.LeftUnknown.Add(name);
            r.Track(name, string.Empty, isSensitive: false, PlaceholderTokenOutcome.LeftUnknown);
            return m.Value; // unknown: untouched
        });

        return result;
    }

    /// <summary>
    /// Live view of every placeholder for the Settings tab: built-ins with
    /// their current values (resolved from <paramref name="app"/> + settings)
    /// and customs (sensitive values masked by the CALLER — this returns the
    /// resolved value so the preview column can offer reveal-on-demand).
    /// </summary>
    public static IReadOnlyList<PlaceholderInfo> Snapshot(AppSection? app)
    {
        var list = new List<PlaceholderInfo>();
        foreach (var name in BuiltInNames)
        {
            TryResolveBuiltIn(name, app ?? new AppSection(), out var value);
            list.Add(new PlaceholderInfo(name, value, IsBuiltIn: true, IsSensitive: false));
        }
        foreach (var (name, value, sensitive) in CustomsSource())
        {
            if (!IsValidCustomName(name)) continue;
            list.Add(new PlaceholderInfo(name, value ?? string.Empty, IsBuiltIn: false, IsSensitive: sensitive));
        }
        return list;
    }

    private static bool TryResolveBuiltIn(string name, AppSection app, out string value)
    {
        // Case-insensitive match against the canonical names; {{Date}} and
        // {{Author}} compute at expansion, endpoint folders come from settings.
        value = name.ToLowerInvariant() switch
        {
            "company"        => app.Company    ?? string.Empty,
            "name"           => app.Name       ?? string.Empty,
            "dotversion"     => app.DotVersion ?? string.Empty,
            "version"        => app.Version    ?? string.Empty,
            "language"       => app.Language   ?? string.Empty,
            "exefile"        => app.EXEFile    ?? string.Empty,
            "msifile"        => app.MSIFile    ?? string.Empty,
            "guid"           => app.GUID       ?? string.Empty,
            "date"           => SystemClock.Now.ToIsoDateOnly(),
            "author"         => Environment.UserName,
            "tagfolder"      => UserDefaultsService.EndpointTagFolder,
            "localappfolder" => UserDefaultsService.EndpointLocalAppFolder,
            _                => null!,
        };
        return value is not null;
    }
}

/// <summary>One row of the placeholder snapshot (Settings tab).</summary>
public sealed record PlaceholderInfo(string Name, string Value, bool IsBuiltIn, bool IsSensitive);

/// <summary>What became of one distinct token name during an expand pass.</summary>
public enum PlaceholderTokenOutcome { Replaced, LeftEmpty, LeftUnknown }

/// <summary>
/// One distinct token the expand pass encountered: how often it occurred,
/// what value it resolves to, and whether that value is sensitive (the UI
/// must show dots, never the plaintext).
/// </summary>
public sealed class PlaceholderTokenUse
{
    public PlaceholderTokenUse(string name, string value, bool isSensitive, PlaceholderTokenOutcome outcome)
    {
        Name = name; Value = value; IsSensitive = isSensitive; Outcome = outcome;
    }

    public string Name { get; }

    /// <summary>Resolved value (empty for LeftEmpty/LeftUnknown). For sensitive
    /// tokens this is the real plaintext — display layers redact it.</summary>
    public string Value { get; }

    public bool IsSensitive { get; }
    public PlaceholderTokenOutcome Outcome { get; }

    /// <summary>Occurrences of this token across the scanned content.</summary>
    public int Count { get; internal set; } = 1;
}

/// <summary>What an <see cref="PlaceholderService.Expand(string, AppSection, out PlaceholderExpandReport)"/> pass did.</summary>
public sealed class PlaceholderExpandReport
{
    /// <summary>Occurrences replaced (built-in + custom).</summary>
    public int Replaced { get; internal set; }

    /// <summary>Custom names found but left literal because their value is empty.</summary>
    public List<string> LeftEmpty { get; } = new();

    /// <summary>Names that matched no placeholder and were left untouched.</summary>
    public List<string> LeftUnknown { get; } = new();

    /// <summary>Sensitive custom names whose plaintext was written into the output.</summary>
    public List<string> SensitiveReplaced { get; } = new();

    public bool TouchedSensitive => SensitiveReplaced.Count > 0;

    /// <summary>
    /// Per-token mapping (first-seen order): each distinct name with its
    /// occurrence count, resolved value and outcome. Drives the replace
    /// preview dialog.
    /// </summary>
    public List<PlaceholderTokenUse> Tokens { get; } = new();

    /// <summary>Counts one occurrence of <paramref name="name"/> (merged case-insensitively).</summary>
    internal void Track(string name, string value, bool isSensitive, PlaceholderTokenOutcome outcome)
    {
        var existing = Tokens.FirstOrDefault(
            t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            Tokens.Add(new PlaceholderTokenUse(name, value, isSensitive, outcome));
        else
            existing.Count++;
    }
}
