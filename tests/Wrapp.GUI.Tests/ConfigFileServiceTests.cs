using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

public class ConfigFileServiceTests
{
    // Minimal valid JSON with all top-level sections present but empty
    private const string MinimalJson = """
        {
          "App": { "Name": "TestApp", "Version": "1.0.0", "Company": "Contoso" },
          "Script": {},
          "SCCMSite": { "Comment": "sites" },
          "IntuneTenant": { "Comment": "tenants" },
          "Domain": { "Comment": "domains" }
        }
        """;

    // -------------------------------------------------------------------
    // Basic deserialize
    // -------------------------------------------------------------------

    [Fact]
    public void DeserializeFromJson_MinimalJson_PopulatesAppName()
    {
        var model = ConfigFileService.DeserializeFromJson(MinimalJson);
        Assert.Equal("TestApp", model.App.Name);
    }

    [Fact]
    public void DeserializeFromJson_MinimalJson_PopulatesVersion()
    {
        var model = ConfigFileService.DeserializeFromJson(MinimalJson);
        Assert.Equal("1.0.0", model.App.Version);
    }

    [Fact]
    public void DeserializeFromJson_MinimalJson_EmptyCollections()
    {
        var model = ConfigFileService.DeserializeFromJson(MinimalJson);
        Assert.Empty(model.IntuneTenants);
        Assert.Empty(model.SccmSites);
    }

    [Fact]
    public void DeserializeFromJson_InvalidJson_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => ConfigFileService.DeserializeFromJson("null"));
    }

    // -------------------------------------------------------------------
    // DetectRunning
    // -------------------------------------------------------------------

    [Fact]
    public void DeserializeFromJson_DetectRunning_ParsedCorrectly()
    {
        const string json = """
            {
              "App": {
                "Name": "MyApp",
                "DetectRunning": [
                  { "DisplayName": "My App", "ExeFileName": "myapp.exe", "Process": "myapp" }
                ]
              },
              "Script": {}
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);

        Assert.Single(model.App.DetectRunning);
        var entry = model.App.DetectRunning[0];
        Assert.Equal("My App", entry.DisplayName);
        Assert.Equal("myapp.exe", entry.ExeFileName);
        Assert.Equal("myapp", entry.Process);
    }

    // -------------------------------------------------------------------
    // Intune tenant parsing
    // -------------------------------------------------------------------

    [Fact]
    public void DeserializeFromJson_IntuneTenant_ParsedWithKey()
    {
        const string json = """
            {
              "App": {},
              "Script": {},
              "IntuneTenant": {
                "Comment": "tenants",
                "PROD": {
                  "Domain": "contoso.com",
                  "ClientID": "abc-123",
                  "AuthFlow": "Interactive"
                }
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);

        Assert.Single(model.IntuneTenants);
        var tenant = model.IntuneTenants[0];
        Assert.Equal("PROD", tenant.Key);
        Assert.Equal("contoso.com", tenant.Domain);
        Assert.Equal("abc-123", tenant.ClientID);
        Assert.Equal(AuthFlow.Interactive, tenant.AuthFlow);
    }

    [Fact]
    public void DeserializeFromJson_IntuneTenant_Comment_NotAddedToList()
    {
        const string json = """
            {
              "App": {},
              "Script": {},
              "IntuneTenant": {
                "Comment": "this is a comment, not a tenant"
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);
        Assert.Empty(model.IntuneTenants);
    }

    // -------------------------------------------------------------------
    // SCCM site parsing
    // -------------------------------------------------------------------

    [Fact]
    public void DeserializeFromJson_SCCMSite_ParsedWithDeploymentGroups()
    {
        const string json = """
            {
              "App": {},
              "Script": {},
              "SCCMSite": {
                "Comment": "sites",
                "SITE1": {
                  "AppFolder": "Software\\Apps",
                  "DeploymentGroups": ["DPGroup1", "DPGroup2"]
                }
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);

        Assert.Single(model.SccmSites);
        var site = model.SccmSites[0];
        Assert.Equal("SITE1", site.Key);
        Assert.Equal("Software\\Apps", site.AppFolder);
        Assert.Equal(2, site.DeploymentGroups.Count);
        Assert.Contains("DPGroup1", site.DeploymentGroups);
    }

    // -------------------------------------------------------------------
    // Intune packages
    // -------------------------------------------------------------------

    [Fact]
    public void DeserializeFromJson_IntunePackage_ParsedCorrectly()
    {
        const string json = """
            {
              "App": {},
              "Script": {
                "IntunePackager": {
                  "Tag": "INTUNE",
                  "Packages": [
                    {
                      "AppName": "MyApp 1.0",
                      "InstallCommand": "setup.exe /S",
                      "InstallExperience": "system",
                      "MaximumInstallationTimeInMinutes": 30
                    }
                  ]
                }
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);
        var pkg = model.Script.IntunePackager.Packages[0];

        Assert.Equal("MyApp 1.0", pkg.AppName);
        Assert.Equal("setup.exe /S", pkg.InstallCommand);
        Assert.Equal("system", pkg.InstallExperience);
        Assert.Equal(30, pkg.MaximumInstallationTimeInMinutes);
    }

    // -------------------------------------------------------------------
    // Round-trip: serialize -> deserialize -> same values
    // -------------------------------------------------------------------

    [Fact]
    public void RoundTrip_AppSection_PreservesFields()
    {
        var original = new AppConfigModel();
        original.App.Name        = "RoundTripApp";
        original.App.Version     = "2.3.4";
        original.App.Company     = "ACME";
        original.App.GUID        = "11111111-2222-3333-4444-555555555555";
        original.App.EXEFile     = "setup.exe";

        var json      = ConfigFileService.SerializeToJson(original);
        var restored  = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal(original.App.Name,        restored.App.Name);
        Assert.Equal(original.App.Version,     restored.App.Version);
        Assert.Equal(original.App.Company,     restored.App.Company);
        Assert.Equal(original.App.GUID,        restored.App.GUID);
        Assert.Equal(original.App.EXEFile,     restored.App.EXEFile);
    }

    [Fact]
    public void RoundTrip_DetectRunning_PreservesEntries()
    {
        var original = new AppConfigModel();
        original.App.DetectRunning.Add(new DetectRunningEntry
        {
            DisplayName = "My App",
            ExeFileName = "myapp.exe",
            Process     = "myapp"
        });

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Single(restored.App.DetectRunning);
        Assert.Equal("myapp.exe", restored.App.DetectRunning[0].ExeFileName);
    }

    [Fact]
    public void RoundTrip_IntuneTenant_PreservesFieldsAndRedactsSecret()
    {
        // SerializeToJson deliberately replaces plaintext ClientSecret with
        // ClientSecretSentinel ("ref:settings") so secrets never get
        // written to bundle Config.json. On the load path, sentinel values
        // are normalised to empty -- the real secret lives in the DPAPI
        // settings store and is re-hydrated from there separately. So a
        // simple round-trip cannot preserve the secret, and shouldn't.
        var original = new AppConfigModel();
        original.IntuneTenants.Add(new IntuneTenantEntry
        {
            Key            = "PROD",
            Domain         = "contoso.com",
            ClientID       = "abc-123",
            AuthFlow       = AuthFlow.ClientSecret,
            // Phase 15 (S-6): the field is a SecureString. Wrap the test value
            // so the model's invariant holds; the serializer still writes the
            // sentinel and the loader still drops both sentinel and empty to null.
            ClientSecret   = SecretProtection.ToSecureString("s3cr3t"),
            IntuneWinPath  = @"C:\IntuneWin"
        });

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Single(restored.IntuneTenants);
        var t = restored.IntuneTenants[0];
        Assert.Equal("PROD",          t.Key);
        Assert.Equal("contoso.com",   t.Domain);
        Assert.Equal(AuthFlow.ClientSecret, t.AuthFlow);
        Assert.Equal(@"C:\IntuneWin", t.IntuneWinPath);

        // Security contract: plaintext is redacted on disk.
        Assert.DoesNotContain("s3cr3t", json);
        Assert.Contains(ConfigFileService.ClientSecretSentinel, json);
        // And the loader normalises the sentinel back to null rather
        // than leaving the sentinel string in the in-memory model.
        Assert.Null(t.ClientSecret);
    }

    [Fact]
    public void RoundTrip_SCCMSite_PreservesFields()
    {
        var original = new AppConfigModel();
        var site = new SCCMSiteEntry { Key = "SITE1", AppFolder = "Software" };
        site.DeploymentGroups.Add("DPGroup1");
        original.SccmSites.Add(site);

        var pkg = new SCCMPackageEntry { AppName = "MyApp", SiteCode = "SITE1" };
        pkg.Deployments.Add(new SCCMDeploymentEntry
        {
            AppName       = "MyApp",
            Collection    = "All Workstations",
            DeployAction  = "Install",
            DeployPurpose = "Required"
        });
        original.Script.SCCMPackager.Packages.Add(pkg);

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Single(restored.SccmSites);
        var s = restored.SccmSites[0];
        Assert.Equal("SITE1",    s.Key);
        Assert.Equal("Software", s.AppFolder);
        Assert.Single(s.DeploymentGroups);

        // Deployments now on package
        var rPkg = restored.Script.SCCMPackager.Packages[0];
        Assert.Equal("SITE1", rPkg.SiteCode);
        Assert.Single(rPkg.Deployments);
        Assert.Equal("All Workstations", rPkg.Deployments[0].Collection);
    }

    [Fact]
    public void RoundTrip_IntunePackageDependency_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new IntunePackageEntry { AppName = "MyApp" };
        pkg.Dependencies.Add(new DependencyEntry { AppName = "VCRedist", AutoInstall = true });
        pkg.Supersedence.Add(new SupersedenceEntry { AppName = "MyApp 0.9", SupersedenceType = "Replace" });
        original.Script.IntunePackager.Packages.Add(pkg);

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        var rPkg = restored.Script.IntunePackager.Packages[0];
        Assert.Single(rPkg.Dependencies);
        Assert.Equal("VCRedist", rPkg.Dependencies[0].AppName);
        Assert.True(rPkg.Dependencies[0].AutoInstall);
        Assert.Single(rPkg.Supersedence);
        Assert.Equal("Replace", rPkg.Supersedence[0].SupersedenceType);
    }

    // -------------------------------------------------------------------
    // SerializeToJson produces valid indented JSON
    // -------------------------------------------------------------------

    [Fact]
    public void SerializeToJson_ProducesIndentedJson()
    {
        var model = new AppConfigModel();
        model.App.Name = "Test";

        var json = ConfigFileService.SerializeToJson(model);

        Assert.Contains("\n", json);
        Assert.Contains("\"Name\":", json);
    }

    [Fact]
    public void SerializeToJson_AlwaysIncludesTopLevelSections()
    {
        var model = new AppConfigModel();
        var json  = ConfigFileService.SerializeToJson(model);

        Assert.Contains("\"App\"", json);
        Assert.Contains("\"Script\"", json);
        Assert.Contains("\"SCCMSite\"", json);
        Assert.Contains("\"IntuneTenant\"", json);
        Assert.Contains("\"Domain\"", json);
    }

    // -------------------------------------------------------------------
    // Per-tenant package model
    // -------------------------------------------------------------------

    [Fact]
    public void RoundTrip_IntunePackage_TenantId_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new IntunePackageEntry { AppName = "MyApp", TenantId = "tenant-guid-1" };
        original.Script.IntunePackager.Packages.Add(pkg);

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        var rPkg = restored.Script.IntunePackager.Packages[0];
        Assert.Equal("tenant-guid-1", rPkg.TenantId);
    }

    [Fact]
    public void RoundTrip_IntunePackage_Assignments_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new IntunePackageEntry
        {
            AppName  = "MyApp",
            TenantId = "tenant-guid-1",
            PackageId = "pkg-guid-1"
        };
        pkg.Assignments.Add(new AssignmentEntry
        {
            AppName   = "MyApp",
            PackageId = "pkg-guid-1",
            Intent    = "required",
            Type      = "Group",
            GroupID   = "group-guid-1",
            GroupMode = "include"
        });
        original.Script.IntunePackager.Packages.Add(pkg);

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        var rPkg = restored.Script.IntunePackager.Packages[0];
        Assert.Single(rPkg.Assignments);
        Assert.Equal("required", rPkg.Assignments[0].Intent);
        Assert.Equal("group-guid-1", rPkg.Assignments[0].GroupID);
    }

    [Fact]
    public void RoundTrip_SCCMPackage_SiteCode_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new SCCMPackageEntry { AppName = "MyApp", SiteCode = "CB1" };
        original.Script.SCCMPackager.Packages.Add(pkg);

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal("CB1", restored.Script.SCCMPackager.Packages[0].SiteCode);
    }

    [Fact]
    public void RoundTrip_SCCMPackage_Deployments_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new SCCMPackageEntry { AppName = "MyApp", SiteCode = "CB1" };
        pkg.Deployments.Add(new SCCMDeploymentEntry
        {
            AppName       = "MyApp",
            Collection    = "All Servers",
            DeployAction  = "Install",
            DeployPurpose = "Available"
        });
        original.Script.SCCMPackager.Packages.Add(pkg);

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        var rPkg = restored.Script.SCCMPackager.Packages[0];
        Assert.Single(rPkg.Deployments);
        Assert.Equal("All Servers", rPkg.Deployments[0].Collection);
        Assert.Equal("Available", rPkg.Deployments[0].DeployPurpose);
    }

    // -------------------------------------------------------------------
    // Migration from old format
    // -------------------------------------------------------------------

    [Fact]
    public void Migration_TargetTenants_ToTenantId()
    {
        const string json = """
            {
              "App": {},
              "Script": {
                "IntunePackager": {
                  "Packages": [
                    { "AppName": "MyApp", "TargetTenants": ["tenant-1", "tenant-2"] }
                  ]
                }
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);
        var pkg = model.Script.IntunePackager.Packages[0];

        Assert.Equal("tenant-1", pkg.TenantId);
    }

    [Fact]
    public void Migration_TargetSites_ToSiteCode()
    {
        const string json = """
            {
              "App": {},
              "Script": {
                "SCCMPackager": {
                  "Packages": [
                    { "AppName": "MyApp", "TargetSites": ["CB1", "LCB"] }
                  ]
                }
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);
        var pkg = model.Script.SCCMPackager.Packages[0];

        Assert.Equal("CB1", pkg.SiteCode);
    }

    [Fact]
    public void Migration_TenantAssignments_MovedToPackage()
    {
        const string json = """
            {
              "App": {},
              "Script": {
                "IntunePackager": {
                  "Packages": [
                    { "AppName": "MyApp", "PackageId": "pkg-1", "TargetTenants": ["PROD"] }
                  ]
                }
              },
              "IntuneTenant": {
                "PROD": {
                  "Domain": "contoso.com",
                  "Assignments": [
                    { "AppName": "MyApp", "PackageId": "pkg-1", "Intent": "required", "GroupID": "grp-1", "GroupMode": "include" }
                  ]
                }
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);
        var pkg = model.Script.IntunePackager.Packages[0];

        Assert.Equal("PROD", pkg.TenantId);
        Assert.Single(pkg.Assignments);
        Assert.Equal("required", pkg.Assignments[0].Intent);
        Assert.Equal("grp-1", pkg.Assignments[0].GroupID);
    }

    [Fact]
    public void Migration_SiteDeployments_MovedToPackage()
    {
        const string json = """
            {
              "App": {},
              "Script": {
                "SCCMPackager": {
                  "Packages": [
                    { "AppName": "MyApp", "PackageId": "pkg-1", "TargetSites": ["CB1"] }
                  ]
                }
              },
              "SCCMSite": {
                "CB1": {
                  "AppFolder": "Software",
                  "Deployments": [
                    { "AppName": "MyApp", "PackageId": "pkg-1", "Collection": "All PCs", "DeployAction": "Install" }
                  ]
                }
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);
        var pkg = model.Script.SCCMPackager.Packages[0];

        Assert.Equal("CB1", pkg.SiteCode);
        Assert.Single(pkg.Deployments);
        Assert.Equal("All PCs", pkg.Deployments[0].Collection);
    }

    [Fact]
    public void Migration_OrphanedAssignment_DoesNotCrash()
    {
        const string json = """
            {
              "App": {},
              "Script": {
                "IntunePackager": { "Packages": [] }
              },
              "IntuneTenant": {
                "PROD": {
                  "Assignments": [
                    { "AppName": "NonExistent", "Intent": "required" }
                  ]
                }
              }
            }
            """;

        var model = ConfigFileService.DeserializeFromJson(json);
        Assert.Empty(model.Script.IntunePackager.Packages);
    }

    [Fact]
    public void NewFormat_TenantHasNoAssignments()
    {
        var original = new AppConfigModel();
        original.IntuneTenants.Add(new IntuneTenantEntry { Key = "PROD", Domain = "contoso.com" });
        var pkg = new IntunePackageEntry { AppName = "MyApp", TenantId = "PROD" };
        pkg.Assignments.Add(new AssignmentEntry { AppName = "MyApp", Intent = "available" });
        original.Script.IntunePackager.Packages.Add(pkg);

        var json = ConfigFileService.SerializeToJson(original);

        // Tenant section should NOT contain Assignments
        Assert.DoesNotContain("\"Assignments\"", json.Split("IntuneTenant")[1].Split("Domain")[0]);
        // Package section SHOULD contain Assignments
        Assert.Contains("\"Assignments\"", json.Split("\"Packages\"")[1]);
    }

    // -------------------------------------------------------------------
    // Per-tenant package fields
    // -------------------------------------------------------------------

    [Fact]
    public void RoundTrip_IntunePackage_ScopeTagsPreserved()
    {
        var original = new AppConfigModel();
        var pkg = new IntunePackageEntry { AppName = "Test" };
        pkg.ScopeTags.Add(new TagEntry { Name = "Scope1" });
        original.Script.IntunePackager.Packages.Add(pkg);

        var json = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);
        var rpkg = restored.Script.IntunePackager.Packages[0];
        Assert.Single(rpkg.ScopeTags);
        Assert.Equal("Scope1", rpkg.ScopeTags[0].Name);
    }

    [Fact]
    public void RoundTrip_SCCMPackage_SiteCodeAndDeployments_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new SCCMPackageEntry { AppName = "Test", SiteCode = "PCB" };
        pkg.Deployments.Add(new SCCMDeploymentEntry { Collection = "All Systems" });
        original.Script.SCCMPackager.Packages.Add(pkg);

        var json = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);
        var rpkg = restored.Script.SCCMPackager.Packages[0];
        Assert.Equal("PCB", rpkg.SiteCode);
        Assert.Single(rpkg.Deployments);
        Assert.Equal("All Systems", rpkg.Deployments[0].Collection);
    }

    [Fact]
    public void RoundTrip_IntunePackage_Dependencies_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new IntunePackageEntry { AppName = "Test" };
        pkg.Dependencies.Add(new DependencyEntry { AppName = "Dep1", AutoInstall = true });
        original.Script.IntunePackager.Packages.Add(pkg);

        var json = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);
        Assert.Single(restored.Script.IntunePackager.Packages[0].Dependencies);
        Assert.True(restored.Script.IntunePackager.Packages[0].Dependencies[0].AutoInstall);
    }

    [Fact]
    public void RoundTrip_IntunePackage_Supersedence_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new IntunePackageEntry { AppName = "Test" };
        pkg.Supersedence.Add(new SupersedenceEntry { AppName = "Old", SupersedenceType = "Replace" });
        original.Script.IntunePackager.Packages.Add(pkg);

        var json = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);
        Assert.Single(restored.Script.IntunePackager.Packages[0].Supersedence);
        Assert.Equal("Replace", restored.Script.IntunePackager.Packages[0].Supersedence[0].SupersedenceType);
    }

    [Fact]
    public void RoundTrip_AppInstallerFiles_EXEFileAndMSIFile_Preserved()
    {
        // Regression guard for 0.6.0.0180: the Import-to-Wrapp overlay
        // guard bug (configInBundle != configPath) wiped EXEFile from the
        // saved Config.json. Pinning the round-trip contract here so any
        // future serializer change can't silently drop these fields.
        var original = new AppConfigModel();
        original.App.Name    = "DotNet";
        original.App.EXEFile = "dotnet-installer.exe";
        original.App.MSIFile = "dotnet.msi";
        original.App.IconFile = "Icon/app.png";

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Equal("dotnet-installer.exe", restored.App.EXEFile);
        Assert.Equal("dotnet.msi",           restored.App.MSIFile);
        Assert.Equal("Icon/app.png",         restored.App.IconFile);
    }

    [Fact]
    public void RoundTrip_IntunePackage_ReturnCodes_Preserved()
    {
        var original = new AppConfigModel();
        var pkg = new IntunePackageEntry { AppName = "Test" };
        pkg.CustomReturnCodes.Add(new ReturnCodeEntry { ReturnCode = 3010, Type = "softReboot" });
        original.Script.IntunePackager.Packages.Add(pkg);

        var json = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);
        Assert.Single(restored.Script.IntunePackager.Packages[0].CustomReturnCodes);
        Assert.Equal(3010, restored.Script.IntunePackager.Packages[0].CustomReturnCodes[0].ReturnCode);
    }
}
