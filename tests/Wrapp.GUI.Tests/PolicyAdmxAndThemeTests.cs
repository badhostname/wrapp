using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wrapp.Services;
using Wrapp.Services.Policy;

namespace Wrapp.Tests;

/// <summary>Locates repo files from the test bin dir (walks up to the root).</summary>
internal static class RepoFiles
{
    public static string Root
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "Wrapp.sln"))
                                   && !Directory.Exists(Path.Combine(dir, "src", "Wrapp.GUI")))
                dir = Directory.GetParent(dir)?.FullName;
            return dir ?? throw new InvalidOperationException("Repo root not found above test bin");
        }
    }
}

/// <summary>
/// Drift guard: the committed ADMX must describe only policies the app
/// actually reads (catalog + meta), and the security-critical set must be
/// present. Renaming a catalog key without touching the ADMX (or vice versa)
/// fails the build — the same contract-test pattern as help keys.
/// </summary>
public class AdmxDriftTests
{
    private static XDocument Admx()
        => XDocument.Load(Path.Combine(RepoFiles.Root, "policy", "Wrapp.admx"));

    private static HashSet<string> AdmxValueNames()
    {
        var doc = Admx();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in doc.Descendants().Attributes("valueName"))
            names.Add(attr.Value);
        return names;
    }

    [Fact]
    public void EveryAdmxValueName_ExistsInTheCatalog()
    {
        foreach (var name in AdmxValueNames())
            Assert.True(PolicyCatalog.Find(name) is not null,
                $"ADMX valueName '{name}' has no PolicyCatalog entry — the app would ignore it");
    }

    [Fact]
    public void SecurityCriticalPolicies_ArePresentInTheAdmx()
    {
        var names = AdmxValueNames();
        foreach (var required in new[]
        {
            "UpdateFeedUrl", "UpdateMode", "KeyVaultRepoUrl", "EnableAzureDevOpsKeyVault",
            "KeyVaultUsePullRequests", "OrgDefaultsPath", "ThemeFilePath",
            "DisableSettingsImport", "DisableOrgDefaultsImport", "Theme",
            "EndpointTagFolder", "EndpointLocalAppFolder",
        })
            Assert.Contains(required, names);
    }

    [Fact]
    public void AdmlDefinesEveryStringTheAdmxReferences()
    {
        var admx = Admx().ToString();
        var adml = File.ReadAllText(Path.Combine(RepoFiles.Root, "policy", "en-US", "Wrapp.adml"));
        foreach (Match m in Regex.Matches(admx, @"\$\((string|presentation)\.([A-Za-z0-9_]+)\)"))
        {
            var kind = m.Groups[1].Value == "string" ? "string" : "presentation";
            Assert.Contains($"<{kind} id=\"{m.Groups[2].Value}\"", adml);
        }
    }
}

/// <summary>
/// Theme file schema validation (pure, no WPF) and the Dark/Light key parity
/// the audit found and the theme engine depends on — pinned forever.
/// </summary>
public class ThemeEngineTests
{
    // ------------------------------------------------------------------
    // .wrapptheme.json schema
    // ------------------------------------------------------------------

    [Fact]
    public void ValidTheme_Parses()
    {
        var theme = WrappThemeFile.TryParse("""
            { "Name": "Contoso", "BaseTheme": "Dark",
              "Colors": { "AccentBrush": "#2D6BC4", "AppBgBrush": "#101418" },
              "ShadowOpacity": 0.4 }
            """, out var error);
        Assert.Null(error);
        Assert.NotNull(theme);
        Assert.Equal("Contoso", theme!.Name);
        Assert.Equal(2, theme.Colors.Count);
    }

    [Theory]
    [InlineData("""{ "BaseTheme": "Dark" }""", "Name")]
    [InlineData("""{ "Name": "X", "BaseTheme": "Neon" }""", "BaseTheme")]
    [InlineData("""{ "Name": "X", "BaseTheme": "Dark", "Colors": { "AccentBrush": "notacolor" } }""", "AccentBrush")]
    [InlineData("""{ "Name": "X", "BaseTheme": "Dark", "ShadowOpacity": 3 }""", "ShadowOpacity")]
    [InlineData("not json at all", "JSON")]
    public void InvalidTheme_IsRejected_WithNamedError(string json, string expectedInError)
    {
        var theme = WrappThemeFile.TryParse(json, out var error);
        Assert.Null(theme);
        Assert.Contains(expectedInError, error);
    }

    // ------------------------------------------------------------------
    // Dark/Light parity (the base-theme contract custom themes rely on)
    // ------------------------------------------------------------------

    private static HashSet<string> KeysOf(string themeFile)
    {
        var path = Path.Combine(RepoFiles.Root, "src", "Wrapp.GUI", "Themes", themeFile);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(File.ReadAllText(path), @"x:Key=""([^""]+)"""))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    [Fact]
    public void DarkAndLight_DefineIdenticalKeySets()
    {
        var dark = KeysOf("Dark.xaml");
        var light = KeysOf("Light.xaml");

        var onlyDark = dark.Except(light).OrderBy(x => x).ToList();
        var onlyLight = light.Except(dark).OrderBy(x => x).ToList();

        Assert.True(onlyDark.Count == 0 && onlyLight.Count == 0,
            $"Theme key asymmetry — only in Dark: [{string.Join(", ", onlyDark)}]; " +
            $"only in Light: [{string.Join(", ", onlyLight)}]");
        Assert.True(dark.Count > 100, "Sanity: expected the full theme key catalog");
    }

    [Fact]
    public void AccentBrush_ExistsInBothThemes()
    {
        // The engine reads the accent FROM the dictionary (replacing the old
        // hardcoded ternary) — the key must exist in every base.
        Assert.Contains("AccentBrush", KeysOf("Dark.xaml"));
        Assert.Contains("AccentBrush", KeysOf("Light.xaml"));
    }
}
