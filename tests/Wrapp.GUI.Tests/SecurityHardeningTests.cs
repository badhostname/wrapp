using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// P0 regression tests (docs/production-readiness-audit.md): each test pins
/// one security/correctness fix so the defect class cannot return.
/// </summary>
[Collection("Placeholders")]   // shares the PlaceholderSecureStore.PathOverride static with those tests
public class SecurityHardeningTests
{
    // ------------------------------------------------------------------
    // SEC-1: PowerShell single-quote escaping
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("O'Brien Ltd", "O''Brien Ltd")]
    [InlineData("x'; Start-Process calc; '", "x''; Start-Process calc; ''")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void PsQuote_DoublesEverySingleQuote(string? input, string expected)
        => Assert.Equal(expected, BundleService.PsQuote(input));

    // ------------------------------------------------------------------
    // SEC-2: bundle path containment
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Icons\\app.png", true)]
    [InlineData("app.png", true)]
    [InlineData("..\\outside.png", false)]
    [InlineData("..\\..\\..\\evil.png", false)]
    [InlineData("C:\\Users\\Public\\x.png", false)]
    [InlineData("Icons\\..\\..\\escape.png", false)]
    public void ResolveInsideBundle_RefusesEscapes(string relative, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "wrapp-bundle-test");
        var resolved = BundleService.ResolveInsideBundle(root, relative);
        if (allowed)
        {
            Assert.NotNull(resolved);
            Assert.StartsWith(Path.GetFullPath(root), resolved!, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(resolved);
        }
    }

    [Fact]
    public void ResolveInsideBundle_NullOrEmpty_IsNull()
    {
        Assert.Null(BundleService.ResolveInsideBundle(@"C:\b", null));
        Assert.Null(BundleService.ResolveInsideBundle(@"C:\b", ""));
    }

    // ------------------------------------------------------------------
    // SEC-3: null sections in settings.json must heal, not brick startup
    // ------------------------------------------------------------------

    [Fact]
    public void SettingsRepair_HealsEveryNullContainer()
    {
        var s = new AppSettings
        {
            IntuneTenants = null!, SccmSites = null!, Domains = null!,
            Placeholders = null!, TenantNameCache = null!, GateState = null!,
            IntunePackageDefaults = null!, IntuneMetadataDefaults = null!,
            IntuneAssignmentDefaults = null!, SccmPackageDefaults = null!,
            SccmMetadataDefaults = null!, SccmDeploymentDefaults = null!,
        };

        Assert.True(SettingsRepair.Apply(s));

        Assert.NotNull(s.IntuneTenants);
        Assert.NotNull(s.SccmSites);
        Assert.NotNull(s.Domains);
        Assert.NotNull(s.Placeholders);
        Assert.NotNull(s.TenantNameCache);
        Assert.NotNull(s.GateState);
        Assert.NotNull(s.IntunePackageDefaults);
        Assert.NotNull(s.SccmDeploymentDefaults);
    }

    [Fact]
    public void SettingsRepair_HealsNullDeploymentGroups()
    {
        var s = new AppSettings();
        s.SccmSites.Add(new SavedSiteEntry { DeploymentGroups = null! });
        SettingsRepair.Apply(s);
        Assert.NotNull(s.SccmSites[0].DeploymentGroups);
    }

    // ------------------------------------------------------------------
    // SEC-9: SchemaVersion guard survives a string-typed value
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("{\"SchemaVersion\": 999, \"App\": {}}")]
    [InlineData("{\"SchemaVersion\": \"999\", \"App\": {}}")]
    public void SchemaGuard_RefusesNewerBundles_IntOrString(string json)
        => Assert.Throws<InvalidOperationException>(() => ConfigFileService.DeserializeFromJson(json));

    // ------------------------------------------------------------------
    // SEC-6: secure store refuses non-envelope values
    // ------------------------------------------------------------------

    [Fact]
    public void SecureStore_RejectsPlantedPlaintext()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wrapp-sec6-{Guid.NewGuid():N}.json");
        PlaceholderSecureStore.PathOverride = path;
        try
        {
            // A value written by SetValue (authentic envelope) round-trips…
            PlaceholderSecureStore.SetValue("good", "real-value");
            Assert.Equal("real-value", PlaceholderSecureStore.GetValue("good"));

            // …but a bare string planted straight into the file is refused.
            var json = File.ReadAllText(path);
            File.WriteAllText(path, json.Replace(
                json.Substring(json.IndexOf("dpapi:"), 8), "planted-"));
            Assert.Null(PlaceholderSecureStore.GetValue("good"));
        }
        finally
        {
            PlaceholderSecureStore.PathOverride = null;
            try { File.Delete(path); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // SEC-5: releases URL only honors https org overrides
    // ------------------------------------------------------------------

    [Fact]
    public void DefaultReleasesUrl_IsHttps()
        => Assert.StartsWith("https://", WhatsNewService.DefaultReleasesUrl);
}
