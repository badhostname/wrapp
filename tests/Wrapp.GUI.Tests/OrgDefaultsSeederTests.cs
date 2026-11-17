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

    [Fact]
    public void Apply_SeedsTheBundleBlock_OnlyOntoFactoryValues()
    {
        var fresh = new AppSettings();
        var org = new OrgDefaults
        {
            Bundle = new OrgBundleDefaults
            {
                DirectoryFormat = @"{Company}\{Name}\{DotVersion}",
                FolderNameSpaces = BundleService.FolderSpacesUnderscore,
                BundleSaveMode = BundleService.SaveModeDirect,
                BundleOutputRoot = @"\\dist\packages$\Template",
            },
        };

        Assert.True(OrgDefaultsSeeder.Apply(fresh, org));
        Assert.Equal(@"{Company}\{Name}\{DotVersion}", fresh.DirectoryFormat);
        Assert.Equal(BundleService.FolderSpacesUnderscore, fresh.FolderNameSpaces);
        Assert.Equal(BundleService.SaveModeDirect, fresh.BundleSaveMode);
        Assert.Equal(@"\\dist\packages$\Template", fresh.BundleOutputRoot);

        var mine = new AppSettings
        {
            DirectoryFormat = @"{Name}\{Version}",
            BundleSaveMode = BundleService.SaveModeConvention,
            BundleOutputRoot = @"D:\MyBundles",
        };
        // Convention IS the factory value, so the org's Direct applies there; the technician's own format and root stand.
        Assert.True(OrgDefaultsSeeder.Apply(mine, org));
        Assert.Equal(@"{Name}\{Version}", mine.DirectoryFormat);
        Assert.Equal(@"D:\MyBundles", mine.BundleOutputRoot);
        Assert.Equal(BundleService.SaveModeDirect, mine.BundleSaveMode);
    }

    [Fact]
    public void Apply_SeedsTheIntuneWinOutputValues_OnlyOntoFactoryValues()
    {
        var fresh = new AppSettings();
        var org = new OrgDefaults
        {
            Bundle = new OrgBundleDefaults
            {
                IntuneWinOutputMode = Helpers.IntuneWinOutput.ModeCentral,
                IntuneWinOutputRoot = @"\\dist\intunewin$",
                IntuneWinSubFolderFormat = @"{Name}\{Version}",
                IntuneWinFolderNameSpaces = BundleService.FolderSpacesKeep,
            },
        };

        Assert.True(OrgDefaultsSeeder.Apply(fresh, org));
        Assert.Equal(Helpers.IntuneWinOutput.ModeCentral, fresh.IntuneWinOutputMode);
        Assert.Equal(@"\\dist\intunewin$", fresh.IntuneWinOutputRoot);
        Assert.Equal(@"{Name}\{Version}", fresh.IntuneWinSubFolderFormat);
        Assert.Equal(BundleService.FolderSpacesKeep, fresh.IntuneWinFolderNameSpaces);

        var mine = new AppSettings { IntuneWinOutputRoot = @"D:\MyIntuneWin" };
        Assert.True(OrgDefaultsSeeder.Apply(mine, org));
        Assert.Equal(@"D:\MyIntuneWin", mine.IntuneWinOutputRoot);          // the technician's own root stands
        Assert.Equal(Helpers.IntuneWinOutput.ModeCentral, mine.IntuneWinOutputMode); // the factory mode gives way
    }
}
