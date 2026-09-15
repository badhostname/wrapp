using System.IO;
using System.Text.RegularExpressions;

namespace Wrapp.Tests;

/// <summary>
/// Workstream H7: the help system's guardrails. HelpContent.xaml is the single
/// source of truth for every info popup and Help.*-backed tooltip; these tests
/// keep it and the views from drifting apart again (the 0286→0317 audit found
/// a dead key throwing a user-visible error dialog, 11 orphans, and stale
/// claims that survived ~30 releases unnoticed):
///  1. Every Help.* key referenced from XAML or code EXISTS (a missing key is
///     a runtime error dialog / XamlParseException).
///  2. Every authored key is REFERENCED somewhere (orphans are dead weight and
///     usually mean a lost hookup).
///  3. Org-specific values never ship inside help text.
///  4. Enumerations documented in help track their source-of-truth code lists
///     (detection operators, template tokens).
/// </summary>
public class HelpKeyReferenceTests
{
    // ------------------------------------------------------------------
    // Repo access (same walk-up pattern as MonacoAssetTests)
    // ------------------------------------------------------------------

    private static string GuiSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Wrapp.GUI");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new DirectoryNotFoundException("src/Wrapp.GUI not found walking up from the test base directory");
    }

    private static string HelpContentPath() => Path.Combine(GuiSrcDir(), "Help", "HelpContent.xaml");

    private static readonly Regex DefinedKeyRx = new(
        "x:Key=\"(?<key>Help\\.[^\"]+)\"", RegexOptions.Compiled);

    // XAML: HelpKey="Help.X" (ViewHeader/SectionHeader) and {StaticResource
    // Help.X} (tooltips, FieldLabelRow Help=). C#: ANY quoted "Help.X" literal
    // - covers TryFindResource/FindResource, ShowHelpAsync wrappers, and
    // whatever future helper passes a key by string. Doc-comment lines are
    // stripped from C# first so placeholder examples ("Help.Xxx.Yyy") don't
    // count as references.
    private static readonly Regex[] XamlReferenceRx =
    {
        new("HelpKey=\"(?<key>Help\\.[^\"]+)\"", RegexOptions.Compiled),
        new("\\{StaticResource (?<key>Help\\.[^}\\s]+)\\}", RegexOptions.Compiled),
    };

    private static readonly Regex CsReferenceRx =
        new("\"(?<key>Help\\.[A-Za-z0-9_.]+)\"", RegexOptions.Compiled);

    private static readonly Regex DocCommentLineRx =
        new(@"^\s*///.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static HashSet<string> DefinedKeys()
    {
        var text = File.ReadAllText(HelpContentPath());
        return DefinedKeyRx.Matches(text).Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, List<string>> ReferencedKeys()
    {
        var refs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(GuiSrcDir(), "*.xaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(GuiSrcDir(), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.EndsWith("HelpContent.xaml", StringComparison.OrdinalIgnoreCase));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var isCs = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (isCs) text = DocCommentLineRx.Replace(text, string.Empty);

            var matches = isCs
                ? CsReferenceRx.Matches(text).AsEnumerable()
                : XamlReferenceRx.SelectMany(rx => rx.Matches(text));
            foreach (Match m in matches)
            {
                var key = m.Groups["key"].Value;
                if (!refs.TryGetValue(key, out var list)) refs[key] = list = new List<string>();
                list.Add(Path.GetFileName(file));
            }
        }
        return refs;
    }

    // ------------------------------------------------------------------
    // 1. No dead references - a HelpKey pointing at nothing shows the user
    //    "No help content found for key: ..."; a StaticResource crashes.
    // ------------------------------------------------------------------

    [Fact]
    public void EveryReferencedHelpKey_IsDefined()
    {
        var defined = DefinedKeys();
        var missing = ReferencedKeys()
            .Where(kv => !defined.Contains(kv.Key))
            .Select(kv => $"{kv.Key} (referenced in {string.Join(", ", kv.Value.Distinct())})")
            .OrderBy(s => s)
            .ToList();
        Assert.True(missing.Count == 0,
            "Help keys referenced but not authored in HelpContent.xaml:\n" + string.Join("\n", missing));
    }

    // ------------------------------------------------------------------
    // 2. No orphans - authored keys nobody can reach are dead weight or a
    //    lost hookup. Keys consumed indirectly (documented for future use or
    //    referenced only from other help text) go on the explicit allowlist
    //    with a reason.
    // ------------------------------------------------------------------

    private static readonly HashSet<string> OrphanAllowlist = new(StringComparer.Ordinal)
    {
        // (empty - add "Help.X.Y" entries here with a reason when a key is
        // deliberately unwired, e.g. referenced from dynamically-built UI)
    };

    [Fact]
    public void EveryDefinedHelpKey_IsReferenced()
    {
        var referenced = ReferencedKeys();
        var orphans = DefinedKeys()
            .Where(k => !referenced.ContainsKey(k) && !OrphanAllowlist.Contains(k))
            .OrderBy(s => s)
            .ToList();
        Assert.True(orphans.Count == 0,
            "Help keys authored but referenced nowhere (wire them, delete them, or allowlist with a reason):\n"
            + string.Join("\n", orphans));
    }

    // ------------------------------------------------------------------
    // 3. Org data never ships in help text (the audit found a hardcoded
    //    org path presented as the fixed results location).
    // ------------------------------------------------------------------

    [Fact]
    public void HelpContent_ContainsNoOrgSpecificValues()
    {
        // The needles are deployment-specific, so they live in a gitignored
        // local file (one per line) - the public repo carries no trace of any
        // particular organization. Without the file the guard is a no-op;
        // create tests/org-needles.local.txt beside the repo's tests to arm it.
        var needleFile = Path.Combine(
            Path.GetDirectoryName(GuiSrcDir())!, "..", "tests", "org-needles.local.txt");
        if (!File.Exists(needleFile)) return;
        var text = File.ReadAllText(HelpContentPath());
        foreach (var needle in File.ReadAllLines(needleFile))
            if (!string.IsNullOrWhiteSpace(needle))
                Assert.DoesNotContain(needle.Trim(), text, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // 4. Enumeration drift tripwires: the help must list every value the
    //    code actually accepts (the audit found 8 of 12 operators listed).
    // ------------------------------------------------------------------

    [Fact]
    public void DetectionTestsHelp_ListsEveryValidOperator()
    {
        var vmSource = File.ReadAllText(Path.Combine(GuiSrcDir(), "ViewModels", "DetectionViewModel.cs"));
        var block = Regex.Match(vmSource, @"ValidOperators.*?\};", RegexOptions.Singleline).Value;
        var operators = Regex.Matches(block, "\"(-\\w+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.True(operators.Count >= 12, $"expected the full operator list in DetectionViewModel, found {operators.Count}");

        var help = HelpString("Help.Detection.Tests");
        var missing = operators.Where(op => !help.Contains(op, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "Operators accepted by DetectionViewModel but absent from Help.Detection.Tests: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void TemplateTokensHelp_ListsEveryTokenTheExpanderSupports()
    {
        // Workstream P: the expander's token list lives in ONE place now -
        // PlaceholderService.BuiltInNames is the source of truth the help
        // must track (TemplateService/BundleService merely delegate).
        var tokens = Wrapp.Services.PlaceholderService.BuiltInNames;
        Assert.True(tokens.Length >= 12, $"expected the full built-in list, found {tokens.Length}");

        var help = HelpString("Help.Scripts.TemplateTokens");
        var missing = tokens.Where(t => !help.Contains(t, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "Built-in placeholders the resolver expands but Help.Scripts.TemplateTokens does not document: "
            + string.Join(", ", missing));
    }

    /// <summary>Raw text of one sys:String entry (markdown source, still XML-escaped).</summary>
    private static string HelpString(string key)
    {
        var text = File.ReadAllText(HelpContentPath());
        var m = Regex.Match(text,
            $"x:Key=\"{Regex.Escape(key)}\">(?<body>.*?)</sys:String>",
            RegexOptions.Singleline);
        Assert.True(m.Success, $"help key {key} not found in HelpContent.xaml");
        return m.Groups["body"].Value;
    }
}
