using System.Collections.ObjectModel;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Drift guard for <see cref="PreferencesSync"/>'s in-memory clone paths.
/// Applying a saved preference tenant/site to a bundle must not silently drop
/// a field - most importantly the secret fields, where a dropped
/// <c>ClientSecretCipher</c> would blank a stored secret. This pins exactly
/// which fields the clone carries today, so a future edit that forgets one
/// fails here.
/// <para>
/// NOTE (flagged for a deliberate future decision, not changed here): the
/// clone - like the SettingsService projection - does NOT carry
/// <c>Domain</c> or <c>ScopeTags</c>. Both omissions are consistent across the
/// two paths; whether that is intended is out of scope for a cleanup cycle.
/// </para>
/// </summary>
public class PreferencesSyncTests
{
    [Fact]
    public void OverwriteTenants_CarriesEveryCopiedField_IncludingSecretCipher()
    {
        using var fresh = SecretProtection.ToSecureString("freshly-typed");
        var src = new IntuneTenantEntry
        {
            Key                            = "PROD",
            Name                           = "Production",
            Comment                        = "main tenant",
            ClientID                       = "client-123",
            AuthFlow                       = AuthFlow.ClientSecret,
            ClientSecret                   = fresh,                  // transient SecureString
            ClientSecretCipher             = "dpapi:v2:STORED==",    // stored cipher
            CertThumbprint                 = "ABCDEF",
            Architecture                   = "x64",
            MinimumSupportedWindowsRelease = "W11_22H2",
            IntuneWinPath                  = @"C:\IntuneWin",
            IconFolder                     = "Icons",
        };

        var target = new ObservableCollection<IntuneTenantEntry>();
        var count = PreferencesSync.OverwriteTenants(target, new[] { src });

        Assert.Equal(1, count);
        var clone = Assert.Single(target);
        Assert.NotSame(src, clone);                                 // a real clone, not the same object

        Assert.Equal("PROD",            clone.Key);
        Assert.Equal("Production",      clone.Name);
        Assert.Equal("main tenant",     clone.Comment);
        Assert.Equal("client-123",      clone.ClientID);
        Assert.Equal(AuthFlow.ClientSecret, clone.AuthFlow);
        Assert.Equal("dpapi:v2:STORED==", clone.ClientSecretCipher);  // secret-at-rest preserved
        // STA-2 (2026-07 audit): the transient SecureString is now DEEP-COPIED,
        // not shared. Sharing was a use-after-dispose hazard (the save path
        // disposes the source's instance). The clone must be an independent,
        // content-equal instance.
        Assert.NotSame(fresh, clone.ClientSecret);
        Assert.NotNull(clone.ClientSecret);
        Assert.Equal(fresh.Length, clone.ClientSecret!.Length);
        var srcPlain   = SecretProtection.WithPlaintext(fresh, p => p);
        var clonePlain = SecretProtection.WithPlaintext(clone.ClientSecret!, p => p);
        Assert.Equal(srcPlain, clonePlain);
        Assert.Equal("ABCDEF",          clone.CertThumbprint);
        Assert.Equal("x64",             clone.Architecture);
        Assert.Equal("W11_22H2",        clone.MinimumSupportedWindowsRelease);
        Assert.Equal(@"C:\IntuneWin",   clone.IntuneWinPath);
        Assert.Equal("Icons",           clone.IconFolder);
    }

    [Fact]
    public void OverwriteSites_CarriesEveryCopiedField_IncludingDeploymentGroups()
    {
        var src = new SCCMSiteEntry
        {
            Key        = "S01",
            Comment    = "primary site",
            AppFolder  = "Software",
            IconFolder = "Icons",
        };
        src.DeploymentGroups.Add("All Workstations");
        src.DeploymentGroups.Add("Pilot Ring");

        var target = new ObservableCollection<SCCMSiteEntry>();
        PreferencesSync.OverwriteSites(target, new[] { src });

        var clone = Assert.Single(target);
        Assert.NotSame(src, clone);
        Assert.Equal("S01",          clone.Key);
        Assert.Equal("primary site", clone.Comment);
        Assert.Equal("Software",     clone.AppFolder);
        Assert.Equal("Icons",        clone.IconFolder);
        Assert.Equal(new[] { "All Workstations", "Pilot Ring" }, clone.DeploymentGroups);
        Assert.NotSame(src.DeploymentGroups, clone.DeploymentGroups);   // deep copy, not shared list
    }

    [Fact]
    public void OverwriteDomains_CarriesEveryCopiedField()
    {
        var src = new DomainEntry
        {
            Key        = "DEV.CONTOSO.COM",
            IsDistPath = @"\\server\dist",
            AppFolder  = "Apps",
            TagFolder  = "Tags",
        };

        var target = new ObservableCollection<DomainEntry>();
        PreferencesSync.OverwriteDomains(target, new[] { src });

        var clone = Assert.Single(target);
        Assert.NotSame(src, clone);
        Assert.Equal("DEV.CONTOSO.COM", clone.Key);
        Assert.Equal(@"\\server\dist",  clone.IsDistPath);
        Assert.Equal("Apps",            clone.AppFolder);
        Assert.Equal("Tags",            clone.TagFolder);
    }

    [Fact]
    public void AddMissingTenants_SkipsExistingKeys_AddsOnlyNew()
    {
        var target = new ObservableCollection<IntuneTenantEntry>
        {
            new() { Key = "PROD", Name = "existing" },
        };
        var source = new[]
        {
            new IntuneTenantEntry { Key = "PROD", Name = "should be skipped" },
            new IntuneTenantEntry { Key = "DEV",  Name = "new" },
            new IntuneTenantEntry { Key = "",     Name = "empty key skipped" },
        };

        var added = PreferencesSync.AddMissingTenants(target, source);

        Assert.Equal(1, added);
        Assert.Equal(2, target.Count);
        Assert.Equal("existing", target[0].Name);              // untouched
        Assert.Contains(target, t => t.Key == "DEV" && t.Name == "new");
    }
}
