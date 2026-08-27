using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Workstream P (P1): the shared placeholder resolver, the DPAPI sidecar for
/// sensitive values, org seeding, and log-redaction registration.
/// </summary>
// Serialized with the other placeholder test classes: both swap the static
// PlaceholderService.CustomsSource hook, and parallel classes cross-talk.
[Collection("Placeholders")]
public class PlaceholderTests : IDisposable
{
    private static readonly AppSection App = new()
    {
        Company = "Contoso", Name = "TestApp", DotVersion = "1.2.3", Version = "1_2_3",
    };

    public PlaceholderTests()
    {
        PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();
    }

    public void Dispose()
    {
        PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();
        PlaceholderSecureStore.PathOverride = null;
    }

    private static void SetCustoms(params (string Name, string? Value, bool Sensitive)[] customs)
        => PlaceholderService.CustomsSource = () => customs;

    // ------------------------------------------------------------------
    // Expansion rules
    // ------------------------------------------------------------------

    [Fact]
    public void BuiltIns_Replace_IncludingEmptyValues()
    {
        // Existing template semantics: an empty built-in becomes empty string,
        // never a literal leftover (templates rely on this).
        var result = PlaceholderService.Expand("[{{Name}}][{{Language}}]", App);
        Assert.Equal("[TestApp][]", result);
    }

    [Fact]
    public void Custom_WithValue_IsReplaced()
    {
        SetCustoms(("mytestgroup", "3f2a1c88-0000-0000-0000-000000000001", false));
        var result = PlaceholderService.Expand("id={{mytestgroup}}", App, out var report);
        Assert.Equal("id=3f2a1c88-0000-0000-0000-000000000001", result);
        Assert.Equal(1, report.Replaced);
    }

    [Fact]
    public void Custom_KnownButEmpty_IsLeftLiteral()
    {
        SetCustoms(("pilotgroup", "", false));
        var result = PlaceholderService.Expand("id={{pilotgroup}}", App, out var report);
        Assert.Equal("id={{pilotgroup}}", result);
        Assert.Contains("pilotgroup", report.LeftEmpty);
    }

    [Fact]
    public void Unknown_IsLeftUntouched()
    {
        var result = PlaceholderService.Expand("keep {{nosuchthing}} intact", App, out var report);
        Assert.Equal("keep {{nosuchthing}} intact", result);
        Assert.Contains("nosuchthing", report.LeftUnknown);
    }

    [Fact]
    public void CustomNames_AreCaseInsensitive()
    {
        SetCustoms(("MyGroup", "gid", false));
        Assert.Equal("gid", PlaceholderService.Expand("{{mygroup}}", App));
    }

    [Fact]
    public void Expansion_IsNonRecursive()
    {
        // A value containing a placeholder is emitted verbatim — no nesting.
        SetCustoms(("outer", "literal {{Name}} stays", false));
        Assert.Equal("literal {{Name}} stays", PlaceholderService.Expand("{{outer}}", App));
    }

    [Fact]
    public void SensitiveReplacement_IsReported()
    {
        SetCustoms(("secretpath", @"\\server\hidden", true));
        PlaceholderService.Expand("path={{secretpath}}", App, out var report);
        Assert.True(report.TouchedSensitive);
        Assert.Contains("secretpath", report.SensitiveReplaced);
    }

    [Fact]
    public void ReservedNames_CannotBeShadowedByCustoms()
    {
        SetCustoms(("Name", "HIJACKED", false));
        Assert.Equal("TestApp", PlaceholderService.Expand("{{Name}}", App));
    }

    [Theory]
    [InlineData("mygroup", true)]
    [InlineData("my-group_2", true)]
    [InlineData("Name", false)]        // reserved
    [InlineData("tagfolder", false)]   // reserved, case-insensitive
    [InlineData("has space", false)]
    [InlineData("", false)]
    [InlineData("with{{braces}}", false)]
    public void CustomName_Validation(string name, bool valid)
    {
        Assert.Equal(valid, PlaceholderService.IsValidCustomName(name));
    }

    [Fact]
    public void LegacyApplyTokens_DelegateToTheSharedResolver()
    {
        Assert.Equal("TestApp", TemplateService.ApplyTokens("{{Name}}", App));
        SetCustoms(("orgtoken", "org-value", false));
        Assert.Equal("org-value", TemplateService.ApplyTokens("{{orgtoken}}", App));
    }

    // ------------------------------------------------------------------
    // Secure sidecar (DPAPI)
    // ------------------------------------------------------------------

    [Fact]
    public void SecureStore_RoundTrips_AndRemoves()
    {
        PlaceholderSecureStore.PathOverride = Path.Combine(
            Path.GetTempPath(), $"wrapp-ph-test-{Guid.NewGuid():N}.json");
        try
        {
            PlaceholderSecureStore.SetValue("mysecret", "s3cret-value");
            Assert.Equal("s3cret-value", PlaceholderSecureStore.GetValue("mysecret"));

            // On-disk form is the DPAPI envelope, never plaintext.
            var raw = File.ReadAllText(PlaceholderSecureStore.PathOverride);
            Assert.DoesNotContain("s3cret-value", raw);
            Assert.Contains("dpapi:v2:", raw);

            PlaceholderSecureStore.Remove("mysecret");
            Assert.Null(PlaceholderSecureStore.GetValue("mysecret"));
        }
        finally
        {
            File.Delete(PlaceholderSecureStore.PathOverride!);
        }
    }

    [Fact]
    public void SecureStore_Prune_DropsStaleEntries()
    {
        PlaceholderSecureStore.PathOverride = Path.Combine(
            Path.GetTempPath(), $"wrapp-ph-test-{Guid.NewGuid():N}.json");
        try
        {
            PlaceholderSecureStore.SetValue("keepme", "a-value");
            PlaceholderSecureStore.SetValue("stale", "b-value");
            PlaceholderSecureStore.PruneExcept(new[] { "keepme" });
            Assert.Equal("a-value", PlaceholderSecureStore.GetValue("keepme"));
            Assert.Null(PlaceholderSecureStore.GetValue("stale"));
        }
        finally
        {
            File.Delete(PlaceholderSecureStore.PathOverride!);
        }
    }

    // ------------------------------------------------------------------
    // Org seeding
    // ------------------------------------------------------------------

    [Fact]
    public void OrgSeeding_AddsMissing_NeverOverwrites_SkipsInvalid()
    {
        var settings = new AppSettings();
        settings.Placeholders.Add(new PlaceholderEntry { Name = "existing", Value = "mine" });

        var org = new OrgDefaults();
        org.Placeholders.Add(new PlaceholderEntry { Name = "existing", Value = "org-clobber" });
        org.Placeholders.Add(new PlaceholderEntry { Name = "newtoken", Value = "org-value", Comment = "from org" });
        org.Placeholders.Add(new PlaceholderEntry { Name = "Name", Value = "reserved-skip" });
        org.Placeholders.Add(new PlaceholderEntry { Name = "secretref", Value = "must-not-arrive", IsSensitive = true });

        var changed = OrgDefaultsSeeder.Apply(settings, org);

        Assert.True(changed);
        Assert.Equal("mine", settings.Placeholders.Single(p => p.Name == "existing").Value);
        Assert.Equal("org-value", settings.Placeholders.Single(p => p.Name == "newtoken").Value);
        Assert.DoesNotContain(settings.Placeholders, p => p.Name == "Name");
        // Sensitive org declarations arrive NAME-ONLY: value stripped.
        var sensitive = settings.Placeholders.Single(p => p.Name == "secretref");
        Assert.True(sensitive.IsSensitive);
        Assert.Equal(string.Empty, sensitive.Value);
    }

    // ------------------------------------------------------------------
    // Log redaction of sensitive values
    // ------------------------------------------------------------------

    [Fact]
    public void SensitiveValues_AreRedactedFromLogLines()
    {
        try
        {
            AppLogger.SetSensitiveValuePatterns(new[] { "Sup3rSecretGroupId" });
            var line = AppLogger.Redact("assigning group Sup3rSecretGroupId to package");
            Assert.DoesNotContain("Sup3rSecretGroupId", line);
            Assert.Contains("***REDACTED***", line);
        }
        finally
        {
            AppLogger.SetSensitiveValuePatterns(Array.Empty<string>());
        }
    }

    [Fact]
    public void TinySensitiveValues_AreNotRegistered()
    {
        // A 1-3 char value would shred every log line; the registrar skips them.
        try
        {
            AppLogger.SetSensitiveValuePatterns(new[] { "ab" });
            Assert.Equal("cab crab", AppLogger.Redact("cab crab"));
        }
        finally
        {
            AppLogger.SetSensitiveValuePatterns(Array.Empty<string>());
        }
    }

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    [Fact]
    public void Placeholders_RoundTrip_ThroughSettingsJson()
    {
        var settings = new AppSettings();
        settings.Placeholders.Add(new PlaceholderEntry
            { Name = "mygroup", Value = "gid-123", Comment = "pilot ring" });
        settings.Placeholders.Add(new PlaceholderEntry
            { Name = "vaultpath", IsSensitive = true }); // value stays empty here

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(2, reloaded.Placeholders.Count);
        Assert.Equal("gid-123", reloaded.Placeholders[0].Value);
        Assert.True(reloaded.Placeholders[1].IsSensitive);
        Assert.Equal(string.Empty, reloaded.Placeholders[1].Value);
    }
}
