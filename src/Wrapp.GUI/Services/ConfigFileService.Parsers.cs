using System.Text.Json.Nodes;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

public static partial class ConfigFileService
{
    // -----------------------------------------------------------------------
    // JSON -> AppConfigModel parsers (one method per Config.json section).
    // -----------------------------------------------------------------------

    private static AppSection ParseApp(JsonObject o)
    {
        var a = new AppSection
        {
            ScriptFramework = o.Str("ScriptFramework", "Appease"),
            Comment     = o.Str("Comment"),
            Company     = o.Str("Company"),
            Name        = o.Str("Name"),
            Language    = o.Str("Language"),
            EXEFile     = o.Str("EXEFile"),
            MSIFile     = o.Str("MSIFile"),
            GUID        = o.Str("GUID") is { Length: > 0 } g ? g : o.Str("AppeaseGUID"),
            URL         = o.Str("URL"),
            DotVersion  = o.Str("DotVersion"),
            Version     = o.Str("Version"),
            IconFile    = o.Str("IconFile"),
            // Missing = auto-extracted, so bundles authored before this field
            // keep today's replace-silently behavior on Full applies.
            IconUserChosen = o.Bool("IconUserChosen", false)
        };

        if (o["DetectRunning"] is JsonArray dr)
        {
            foreach (var item in dr.OfType<JsonObject>())
                a.DetectRunning.Add(new DetectRunningEntry
                {
                    DisplayName = item.Str("DisplayName"),
                    ExeFileName = item.Str("ExeFileName"),
                    Process     = item.Str("Process")
                });
        }

        if (o["Dependencies"] is JsonArray deps)
        {
            foreach (var item in deps)
            {
                var s = item?.GetValue<string>();
                if (!string.IsNullOrEmpty(s)) a.Dependencies.Add(s);
            }
        }

        return a;
    }

    private static ScriptSection ParseScript(JsonObject o)
    {
        var s = new ScriptSection
        {
            Comment = o.Str("Comment")
        };

        if (o["Detect"] is JsonObject det)
        {
            s.Detect.Expression_Default = det.Str("Expression_Default");

            // Read additional named expressions (Expression_Project, Expression_Visio, etc.)
            foreach (var prop in det)
            {
                if (prop.Key.StartsWith("Expression_") && prop.Key != "Expression_Default")
                {
                    var suffix = prop.Key.Substring("Expression_".Length);
                    s.Detect.Expressions.Add(new ExpressionEntry
                    {
                        Key = suffix,
                        Expression = prop.Value?.GetValue<string>() ?? string.Empty
                    });
                }
            }

            if (det["Tests"] is JsonArray tests)
            {
                foreach (var t in tests.OfType<JsonObject>())
                    s.Detect.Tests.Add(new DetectionTest
                    {
                        Name     = t.Str("Name"),
                        Symbol   = t.Str("Symbol"),
                        Path     = t.Str("Path"),
                        Command  = t.Str("Command"),
                        Property = t.Str("Property"),
                        Operator = t.Str("Operator"),
                        Value    = t.Str("Value")
                    });
            }
        }

        if (o["Install"] is JsonObject inst)
        {
            s.Install.Tag             = inst.Str("Tag");
            s.Install.UninstallFirst  = inst.Bool("UninstallFirst");
            s.Install.RunAsAdmin      = inst.Bool("RunAsAdmin", true);
            s.Install.DetectApp       = inst.Bool("DetectApp");
            s.Install.CloseRunning    = inst.Bool("CloseRunning");
            s.Install.BackgroundColor = inst.Str("BackgroundColor");
            s.Install.ForegroundColor = inst.Str("ForegroundColor");
        }

        if (o["Uninstall"] is JsonObject uninst)
        {
            s.Uninstall.Tag             = uninst.Str("Tag");
            s.Uninstall.RunAsAdmin      = uninst.Bool("RunAsAdmin", true);
            s.Uninstall.DetectApp       = uninst.Bool("DetectApp");
            s.Uninstall.CloseRunning    = uninst.Bool("CloseRunning");
            s.Uninstall.BackgroundColor = uninst.Str("BackgroundColor");
            s.Uninstall.ForegroundColor = uninst.Str("ForegroundColor");
        }

        if (o["Console"] is JsonObject con)
            s.Console.Tag = con.Str("Tag");

        if (o["IntunePackager"] is JsonObject ip)
            s.IntunePackager = ParseIntunePackagerSection(ip);

        if (o["SCCMPackager"] is JsonObject sp)
            s.SCCMPackager = ParseSCCMPackagerSection(sp);

        return s;
    }

    private static IntunePackagerSection ParseIntunePackagerSection(JsonObject o)
    {
        var s = new IntunePackagerSection
        {
            Tag                 = o.Str("Tag"),
            BackgroundColor     = o.Str("BackgroundColor"),
            ForegroundColor     = o.Str("ForegroundColor"),
            TerminateOnCollision = o.Bool("TerminateOnCollision")
        };

        if (o["Categories"] is JsonArray cats)
        {
            foreach (var c in cats)
            {
                var cs = c?.GetValue<string>();
                if (!string.IsNullOrEmpty(cs)) s.Categories.Add(cs);
            }
        }

        if (o["Packages"] is JsonArray pkgs)
        {
            foreach (var p in pkgs.OfType<JsonObject>())
                s.Packages.Add(ParseIntunePackage(p));
        }

        return s;
    }

    private static IntunePackageEntry ParseIntunePackage(JsonObject o)
    {
        var p = new IntunePackageEntry
        {
            AppName                           = o.Str("AppName"),
            PackageId                         = o.Str("PackageId") is { Length: > 0 } pid ? pid : Guid.NewGuid().ToString(),
            // Missing = enabled, so bundles authored before this field are unaffected.
            IsEnabled                         = o.Bool("IsEnabled", true),
            Comment                           = o.Str("Comment"),
            Notes                             = o.Str("Notes"),
            AppVersion                        = o.Str("AppVersion"),
            IconFile                          = o.Str("IconFile"),
            PackageOption                     = o.Str("PackageOption"),
            UpdateMode                        = o.EnumOr("UpdateMode", UpdateMode.Create),
            ExistingAppID                     = o.Str("ExistingAppID"),
            InstallCommand                    = o.Str("InstallCommand"),
            UninstallCommand                  = o.Str("UninstallCommand"),
            InstallExperience                 = o.Str("InstallExperience"),
            RestartBehavior                   = o.Str("RestartBehavior"),
            MaximumInstallationTimeInMinutes  = o.Int("MaximumInstallationTimeInMinutes", 60),
            AllowAvailableUninstall           = o.Bool("AllowAvailableUninstall"),
            CompanyPortalFeaturedApp          = o.Bool("CompanyPortalFeaturedApp"),
            Developer                         = o.Str("Developer"),
            Owner                             = o.Str("Owner"),
            InformationURL                    = o.Str("InformationURL"),
            PrivacyURL                        = o.Str("PrivacyURL"),
            UseAzCopy                         = o.Bool("UseAzCopy"),
            AzCopyWindowStyle                 = o.Str("AzCopyWindowStyle") is { Length: > 0 } aws ? aws : "Hidden",
            Architecture                      = o.Str("Architecture"),
            MinimumSupportedWindowsRelease    = o.Str("MinimumSupportedWindowsRelease")
        };

        foreach (var c in o.StrArray("Categories")) p.Categories.Add(new TagEntry { Name = c });
        foreach (var t in o.StrArray("ScopeTags"))  p.ScopeTags.Add(new TagEntry { Name = t });

        if (o["DetectionRules"] is JsonArray dr)
        {
            foreach (var r in dr.OfType<JsonObject>())
                p.DetectionRules.Add(new DetectionRuleEntry
                {
                    Type    = r.Str("Type"),
                    RawJson = r.ToJsonString()
                });
        }

        if (o["AdditionalRequirementRules"] is JsonArray arr)
        {
            foreach (var r in arr.OfType<JsonObject>())
                p.AdditionalRequirementRules.Add(new RequirementRuleEntry
                {
                    Type    = r.Str("Type"),
                    RawJson = r.ToJsonString()
                });
        }

        if (o["CustomReturnCodes"] is JsonArray rc)
        {
            foreach (var r in rc.OfType<JsonObject>())
                p.CustomReturnCodes.Add(new ReturnCodeEntry
                {
                    ReturnCode = r.Int("ReturnCode"),
                    Type       = r.Str("Type")
                });
        }

        if (o["Dependencies"] is JsonArray deps)
        {
            foreach (var d in deps.OfType<JsonObject>())
                p.Dependencies.Add(new DependencyEntry
                {
                    AppName     = d.Str("AppName"),
                    AutoInstall = d.Bool("AutoInstall", true)
                });
        }

        if (o["Supersedence"] is JsonArray sup)
        {
            foreach (var s in sup.OfType<JsonObject>())
                p.Supersedence.Add(new SupersedenceEntry
                {
                    AppName          = s.Str("AppName"),
                    SupersedenceType = s.Str("SupersedenceType"),
                    UninstallOldApp  = s.Bool("UninstallOldApp")
                });
        }

        // Single-tenant targeting
        p.TenantId = o.Str("TenantId");
        // Migration: old format had TargetTenants array
        if (string.IsNullOrEmpty(p.TenantId))
        {
            var legacy = o.StrArray("TargetTenants").ToList();
            if (legacy.Count > 0) p.TenantId = legacy[0];
        }

        // Package-level assignments (new format)
        if (o["Assignments"] is JsonArray pkgAssignments)
        {
            foreach (var aNode in pkgAssignments)
            {
                if (aNode is JsonObject aObj)
                    p.Assignments.Add(ParseAssignment(aObj));
            }
        }

        return p;
    }

    private static SCCMPackagerSection ParseSCCMPackagerSection(JsonObject o)
    {
        var s = new SCCMPackagerSection
        {
            Tag = o.Str("Tag")
        };

        if (o["Packages"] is JsonArray pkgs)
        {
            foreach (var p in pkgs.OfType<JsonObject>())
            {
                var entry = new SCCMPackageEntry
                {
                    // Identity
                    AppName                  = p.Str("AppName"),
                    PackageId                = p.Str("PackageId") is { Length: > 0 } pid ? pid : Guid.NewGuid().ToString(),
                    // Missing = enabled (see the Intune parser).
                    IsEnabled                = p.Bool("IsEnabled", true),
                    AppComment               = p.Str("AppComment"),
                    Icon                     = p.Str("Icon"),
                    // New-CMApplication metadata
                    Publisher                = p.Str("Publisher"),
                    SoftwareVersion          = p.Str("SoftwareVersion"),
                    Owner                    = p.Str("Owner"),
                    SupportContact           = p.Str("SupportContact"),
                    Description              = p.Str("Description"),
                    ReleaseDate              = p.Str("ReleaseDate"),
                    LocalizedName            = p.Str("LocalizedName"),
                    LocalizedDescription     = p.Str("LocalizedDescription"),
                    Keywords                 = p.Str("Keywords"),
                    IsFeatured               = p.Bool("IsFeatured"),
                    AutoInstall              = p.Bool("AutoInstall", true),
                    PrivacyUrl               = p.Str("PrivacyUrl"),
                    UserDocumentation        = p.Str("UserDocumentation"),
                    LinkText                 = p.Str("LinkText"),
                    // Add-CMScriptDeploymentType
                    Name                     = p.Str("Name"),
                    Comment                  = p.Str("Comment"),
                    PackageOption            = p.Str("PackageOption"),
                    InstallCommand           = p.Str("InstallCommand"),
                    UninstallCommand         = p.Str("UninstallCommand"),
                    RepairCommand            = p.Str("RepairCommand"),
                    InstallationBehaviorType = p.Str("InstallationBehaviorType"),
                    LogonRequirementType     = p.Str("LogonRequirementType"),
                    InstallBehavior          = p.Bool("InstallBehavior"),
                    UserInteractionMode      = p.Str("UserInteractionMode"),
                    RebootBehavior           = p.Str("RebootBehavior"),
                    EstimatedRuntimeMins     = p.Int("EstimatedRuntimeMins", 15),
                    MaximumAllowedRuntimeMins = p.Int("MaximumAllowedRuntimeMins", 120),
                    SlowNetworkDeploymentMode = p.Str("SlowNetworkDeploymentMode"),
                    ContentFallback          = p.Bool("ContentFallback")
                };

                // Default SlowNetworkDeploymentMode if empty
                if (string.IsNullOrEmpty(entry.SlowNetworkDeploymentMode))
                    entry.SlowNetworkDeploymentMode = "DoNothing";
                // Default UserInteractionMode if empty
                if (string.IsNullOrEmpty(entry.UserInteractionMode))
                    entry.UserInteractionMode = "Hidden";
                // Default RebootBehavior if empty
                if (string.IsNullOrEmpty(entry.RebootBehavior))
                    entry.RebootBehavior = "BasedOnExitCode";

                if (p["InstallBehaviors"] is JsonArray ibs)
                {
                    foreach (var ib in ibs.OfType<JsonObject>())
                        entry.InstallBehaviors.Add(new InstallBehaviorEntry
                        {
                            ExeFileName = ib.Str("ExeFileName"),
                            DisplayName = ib.Str("DisplayName")
                        });
                }

                if (p["Dependencies"] is JsonArray sccmDeps)
                {
                    foreach (var d in sccmDeps.OfType<JsonObject>())
                        entry.Dependencies.Add(new DependencyEntry
                        {
                            AppName     = d.Str("AppName"),
                            AutoInstall = d.Bool("AutoInstall", true)
                        });
                }

                if (p["Supersedence"] is JsonArray sccmSup)
                {
                    foreach (var ss in sccmSup.OfType<JsonObject>())
                        entry.Supersedence.Add(new SupersedenceEntry
                        {
                            AppName          = ss.Str("AppName"),
                            SupersedenceType = ss.Str("SupersedenceType")
                        });
                }

                // Single-site targeting
                entry.SiteCode = p.Str("SiteCode");
                // Migration: old format had TargetSites array
                if (string.IsNullOrEmpty(entry.SiteCode))
                {
                    var legacy = p.StrArray("TargetSites").ToList();
                    if (legacy.Count > 0) entry.SiteCode = legacy[0];
                }

                // Package-level deployments (new format)
                if (p["Deployments"] is JsonArray pkgDeploys)
                {
                    foreach (var dNode in pkgDeploys)
                    {
                        if (dNode is JsonObject dObj)
                            entry.Deployments.Add(ParseSCCMDeployment(dObj));
                    }
                }

                s.Packages.Add(entry);
            }
        }

        return s;
    }

    private static SCCMSiteEntry ParseSCCMSite(string key, JsonObject o)
    {
        var s = new SCCMSiteEntry
        {
            Key        = key,
            Comment    = o.Str("Comment"),
            AppFolder  = o.Str("AppFolder"),
            IconFolder = o.Str("IconFolder")
        };

        foreach (var g in o.StrArray("DeploymentGroups")) s.DeploymentGroups.Add(g);

        // Migration: old format had Deployments on site -- collect for post-load migration
        // (MigrateSiteDeploymentsToPackages handles redistribution)

        return s;
    }

    /// <summary>
    /// SCCM-friendly date load: rejects the Intune "ASAP" sentinel
    /// (<c>0001-01-01T00:00:00.000Z</c> = <c>DateTime.MinValue</c>) by
    /// normalising it to an empty string. SCCM's
    /// <c>New-CMApplicationDeployment</c> cmdlet underflows when
    /// converting MinValue to UTC. The sentinel can land in SCCM bundles
    /// via a prior version's settings UI bug or hand-edits; this load-time
    /// normalisation makes the next save clean.
    /// </summary>
    private static string LoadSccmDate(JsonObject d, string key)
    {
        var raw = d.Str(key);
        if (string.IsNullOrEmpty(raw)) return raw;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                              System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            && dt.Year <= 1)
        {
            return string.Empty;
        }
        return raw;
    }

    private static SCCMDeploymentEntry ParseSCCMDeployment(JsonObject d) => new()
    {
        Label                      = d.Str("Label"),
        Comment                    = d.Str("Comment"),
        AppName                    = d.Str("AppName"),
        PackageId                  = d.Str("PackageId"),
        Collection                 = d.Str("Collection"),
        UserNotification           = d.Str("UserNotification"),
        DeployAction               = d.Str("DeployAction"),
        DeployPurpose              = d.Str("DeployPurpose"),
        AvailableDateTime          = LoadSccmDate(d, "AvailableDateTime"),
        DeadlineDateTime           = LoadSccmDate(d, "DeadlineDateTime"),
        TimeBaseOn                 = d.Str("TimeBaseOn"),
        ApprovalRequired           = d.Bool("ApprovalRequired"),
        OverrideServiceWindow      = d.Bool("OverrideServiceWindow"),
        RebootOutsideServiceWindow = d.Bool("RebootOutsideServiceWindow"),
        SendWakeupPacket           = d.Bool("SendWakeupPacket")
    };

    private static IntuneTenantEntry ParseIntuneTenant(string key, JsonObject o)
    {
        var t = new IntuneTenantEntry
        {
            Key                           = key,
            Name                          = o.Str("Name"),
            Comment                       = o.Str("Comment"),
            Domain                        = o.Str("Domain"),
            ClientID                      = o.Str("ClientID"),
            AuthFlow                      = o.EnumOr("AuthFlow", AuthFlow.Interactive),
            ClientSecret                  = ResolveClientSecretOnLoad(o.Str("ClientSecret")),
            CertThumbprint                = o.Str("CertThumbprint"),
            Architecture                  = o.Str("Architecture"),
            MinimumSupportedWindowsRelease = o.Str("MinimumSupportedWindowsRelease"),
            IntuneWinPath                 = o.Str("IntuneWinPath"),
            IconFolder                    = o.Str("IconFolder")
        };

        foreach (var tag in o.StrArray("ScopeTags")) t.ScopeTags.Add(tag);

        // Note: old-format Assignments on tenants are migrated to packages
        // by MigrateOldAssignmentsToPackages in DeserializeFromJson.

        return t;
    }

    private static AssignmentEntry ParseAssignment(JsonObject o) => new()
    {
        Label                               = o.Str("Label"),
        AppName                             = o.Str("AppName"),
        PackageId                           = o.Str("PackageId"),
        Type                                = o.Str("Type"),
        GroupID                             = o.Str("GroupID"),
        GroupMode                           = o.Str("GroupMode"),
        Intent                              = o.Str("Intent"),
        Notification                        = o.Str("Notification"),
        DeliveryOptimizationPriority        = o.Str("DeliveryOptimizationPriority"),
        AvailableTime                       = o.Str("AvailableTime"),
        DeadlineTime                        = o.Str("DeadlineTime"),
        UseLocalTime                        = o.Str("UseLocalTime"),
        AutoUpdateSupersededApps            = o.Str("AutoUpdateSupersededApps"),
        EnableRestartGracePeriod            = o.Bool("EnableRestartGracePeriod"),
        RestartGracePeriodInMinutes         = o.Str("RestartGracePeriodInMinutes"),
        RestartCountDownDisplayInMinutes    = o.Str("RestartCountDownDisplayInMinutes"),
        RestartNotificationSnoozeInMinutes  = o.Str("RestartNotificationSnoozeInMinutes"),
        FilterName                          = o.Str("FilterName"),
        FilterMode                          = o.Str("FilterMode")
    };

    private static DomainEntry ParseDomain(string key, JsonObject o) => new()
    {
        Key        = key,
        IsDistPath = o.Str("isDistPath"),
        AppFolder  = o.Str("AppFolder"),
        TagFolder  = o.Str("TagFolder")
    };

    // -----------------------------------------------------------------------
    // Serialize helpers
    // -----------------------------------------------------------------------
}
