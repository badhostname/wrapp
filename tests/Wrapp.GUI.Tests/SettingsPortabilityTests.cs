using System.Text.Json;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Settings export/import. The security invariant: an exported file must never
/// carry per-machine secrets or trust approvals (DPAPI ciphertext is useless
/// elsewhere and trust is a per-machine decision by design - SEC-1), and an
/// import must never be able to grant trust on the importing machine.
/// </summary>
public class SettingsPortabilityTests
{
    private static AppSettings ConfiguredProfile() => new()
    {
        DirectoryFormat = @"{Company}\{Name}",
        KeyVaultRepoUrl = "https://dev.azure.com/contoso/_git/keys",
        KeyVaultRepoUrlHash = "dpapi-trust-token-value",
        UpdateFeedUrl = @"\\fileserver\wrapp-updates",
        UpdateFeedTrustToken = "dpapi-feed-token-value",
        UpdateMode = "NotifyOnly",
        LastRunVersion = "0.6.300",
        LastSeenChangelogVersion = "0.6.300",
    };

    [Fact]
    public void Export_StripsTrustTokensAndSecrets()
    {
        var json = SettingsPortability.BuildExportJson(ConfiguredProfile());

        Assert.DoesNotContain("dpapi-trust-token-value", json);
        Assert.DoesNotContain("dpapi-feed-token-value", json);
        foreach (var stripped in SettingsPortability.StrippedProperties)
            Assert.DoesNotContain($"\"{stripped}\"", json);
    }

    [Fact]
    public void Export_KeepsTheConfigurationWorthSharing()
    {
        var json = SettingsPortability.BuildExportJson(ConfiguredProfile());
        Assert.Contains("https://dev.azure.com/contoso/_git/keys", json);
        Assert.Contains("wrapp-updates", json);
        Assert.Contains("NotifyOnly", json);
    }

    [Fact]
    public void Export_RoundTripsAsSettings()
    {
        var json = SettingsPortability.BuildExportJson(ConfiguredProfile());
        var back = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.CaseInsensitive);
        Assert.NotNull(back);
        Assert.Equal("NotifyOnly", back!.UpdateMode);
        // Stripped fields come back as their factory defaults, never the source values.
        Assert.Equal(string.Empty, back.UpdateFeedTrustToken);
        Assert.Equal(string.Empty, back.KeyVaultRepoUrlHash);
    }

    [Fact]
    public void Import_PreservesThisMachinesTrustAndGateState()
    {
        var target = new AppSettings
        {
            KeyVaultRepoUrlHash = "my-machine-vault-token",
            UpdateFeedTrustToken = "my-machine-feed-token",
            LastSeenChangelogVersion = "0.6.305",
        };
        target.GateState["liability-waiver"] = "accepted";

        var imported = new AppSettings
        {
            UpdateFeedUrl = @"\\other\feed",
            UpdateMode = "Disabled",
            KeyVaultRepoUrlHash = "attacker-supplied-token",
            UpdateFeedTrustToken = "attacker-supplied-token",
        };

        SettingsPortability.ApplyImported(target, imported);

        // Configuration came across...
        Assert.Equal(@"\\other\feed", target.UpdateFeedUrl);
        Assert.Equal("Disabled", target.UpdateMode);
        // ...but trust and gate answers are this machine's, untouched.
        Assert.Equal("my-machine-vault-token", target.KeyVaultRepoUrlHash);
        Assert.Equal("my-machine-feed-token", target.UpdateFeedTrustToken);
        Assert.Equal("accepted", target.GateState["liability-waiver"]);
        Assert.Equal("0.6.305", target.LastSeenChangelogVersion);
    }

    [Fact]
    public void ImportedFeedUrl_StillRequiresApproval()
    {
        // End-to-end of the invariant: after importing a feed URL, the update
        // service must still consider the feed unapproved on this machine.
        var target = new AppSettings();
        SettingsPortability.ApplyImported(target, new AppSettings { UpdateFeedUrl = @"\\other\feed" });
        Assert.False(UpdateService.IsFeedTrusted(target.UpdateFeedUrl, target.UpdateFeedTrustToken));
    }
}
