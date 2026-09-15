using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Workstream O: org-defaults seeding semantics. Core invariant: a value is
/// applied ONLY while the technician's setting still equals its factory
/// default -- explicit choices are never overwritten.
/// </summary>
public sealed class OrgDefaultsSeederTests
{
    [Fact]
    public void Apply_EmptyOrgDefaults_ChangesNothing()
    {
        var settings = new AppSettings();
        Assert.False(OrgDefaultsSeeder.Apply(settings, new OrgDefaults()));
    }

    [Fact]
    public void Apply_SeedsVaultAndEndpoint_IntoFreshProfile()
    {
        var settings = new AppSettings();
        var org = new OrgDefaults
        {
            Vault = new OrgVaultDefaults
            {
                KeyVaultRepoUrl = "https://dev.azure.com/contoso/_git/keys",
                KeyVaultUsePullRequests = true,
                KeyVaultPathTemplate = "/org/{Tenant}/{AppId}.json",
            },
            Endpoint = new OrgEndpointDefaults { TagFolder = @"C:\Contoso\Tag" },
        };

        Assert.True(OrgDefaultsSeeder.Apply(settings, org));
        Assert.Equal("https://dev.azure.com/contoso/_git/keys", settings.KeyVaultRepoUrl);
        Assert.True(settings.KeyVaultUsePullRequests);
        Assert.Equal("/org/{Tenant}/{AppId}.json", settings.KeyVaultPathTemplate);
        Assert.Equal(@"C:\Contoso\Tag", settings.EndpointTagFolder);
        // Unspecified fields keep factory values
        Assert.Equal(new AppSettings().KeyVaultManualPathTemplate, settings.KeyVaultManualPathTemplate);
    }

    [Fact]
    public void Apply_NeverOverwrites_TechnicianValues()
    {
        var settings = new AppSettings
        {
            KeyVaultRepoUrl = "https://dev.azure.com/mine/_git/my-keys",
            KeyVaultPathTemplate = "/mine/{AppId}.json",
            EndpointTagFolder = @"D:\MyTag",
        };
        var org = new OrgDefaults
        {
            Vault = new OrgVaultDefaults
            {
                KeyVaultRepoUrl = "https://dev.azure.com/contoso/_git/keys",
                KeyVaultPathTemplate = "/org/{Tenant}/{AppId}.json",
            },
            Endpoint = new OrgEndpointDefaults { TagFolder = @"C:\Contoso\Tag" },
        };

        Assert.False(OrgDefaultsSeeder.Apply(settings, org));
        Assert.Equal("https://dev.azure.com/mine/_git/my-keys", settings.KeyVaultRepoUrl);
        Assert.Equal("/mine/{AppId}.json", settings.KeyVaultPathTemplate);
        Assert.Equal(@"D:\MyTag", settings.EndpointTagFolder);
    }

    [Fact]
    public void Apply_SeedsDefaultsBlock_OnlyWhileFactoryIdentical()
    {
        var settings = new AppSettings();
        var org = new OrgDefaults
        {
            IntunePackageDefaults = new IntunePackageDefaults
            {
                Architecture = "x64",
                InstallExperience = "system",
                MaximumInstallationTimeInMinutes = 90,
            },
        };

        Assert.True(OrgDefaultsSeeder.Apply(settings, org));
        Assert.Equal("x64", settings.IntunePackageDefaults.Architecture);
        Assert.Equal(90, settings.IntunePackageDefaults.MaximumInstallationTimeInMinutes);

        // Technician later changes the block -- a re-run must not clobber it
        settings.IntunePackageDefaults.MaximumInstallationTimeInMinutes = 30;
        var org2 = new OrgDefaults
        {
            IntunePackageDefaults = new IntunePackageDefaults { Architecture = "arm64" },
        };
        Assert.False(OrgDefaultsSeeder.Apply(settings, org2));
        Assert.Equal(30, settings.IntunePackageDefaults.MaximumInstallationTimeInMinutes);
        Assert.Equal("x64", settings.IntunePackageDefaults.Architecture);
    }

    [Fact]
    public void Apply_BoolsApply_OnlyAtFactoryDefault()
    {
        // Technician deliberately turned the vault OFF (factory is ON)
        var settings = new AppSettings { EnableAzureDevOpsKeyVault = false };
        var org = new OrgDefaults
        {
            Vault = new OrgVaultDefaults { EnableAzureDevOpsKeyVault = true },
        };
        Assert.False(OrgDefaultsSeeder.Apply(settings, org));
        Assert.False(settings.EnableAzureDevOpsKeyVault);
    }

    [Fact]
    public void Apply_SeedsUpdateBlock_ButNeverTheTrustToken()
    {
        var settings = new AppSettings();
        var org = new OrgDefaults
        {
            Update = new OrgUpdateDefaults
            {
                FeedUrl = @"\\fileserver\wrapp-updates",
                Mode = "NotifyOnly",
            },
        };

        Assert.True(OrgDefaultsSeeder.Apply(settings, org));
        Assert.Equal(@"\\fileserver\wrapp-updates", settings.UpdateFeedUrl);
        Assert.Equal("NotifyOnly", settings.UpdateMode);
        // The whole point: seeding a feed URL must NOT approve it.
        Assert.Equal(string.Empty, settings.UpdateFeedTrustToken);

        // Technician's explicit mode choice survives a re-run.
        settings.UpdateMode = "Disabled";
        var org2 = new OrgDefaults { Update = new OrgUpdateDefaults { Mode = "Auto" } };
        Assert.False(OrgDefaultsSeeder.Apply(settings, org2));
        Assert.Equal("Disabled", settings.UpdateMode);
    }

    [Fact]
    public void AppLogger_OrgRedactionPatterns_ScrubMatches()
    {
        try
        {
            AppLogger.SetOrgRedactionPatterns(new[] { @"CONTOSO\.COM", "not a (valid regex" });
            var scrubbed = AppLogger.Redact("connecting to prod.contoso.com now");
            Assert.Contains("***ORG-REDACTED***", scrubbed);
            Assert.DoesNotContain("contoso.com", scrubbed, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AppLogger.SetOrgRedactionPatterns(Array.Empty<string>());
        }
    }
}
