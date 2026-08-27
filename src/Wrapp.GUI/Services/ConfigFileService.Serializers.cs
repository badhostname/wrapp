using System.Text.Json.Nodes;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

public static partial class ConfigFileService
{
    // -----------------------------------------------------------------------
    // AppConfigModel -> JSON serializers (one method per Config.json section).
    // -----------------------------------------------------------------------

    private static JsonObject SerializeApp(AppSection a) => new()
    {
        ["ScriptFramework"] = JsonValue.Create(a.ScriptFramework),
        ["Comment"]     = JsonValue.Create(a.Comment),
        ["Company"]     = JsonValue.Create(a.Company),
        ["Name"]        = JsonValue.Create(a.Name),
        ["Language"]    = JsonValue.Create(a.Language),
        ["EXEFile"]     = JsonValue.Create(a.EXEFile),
        ["MSIFile"]     = JsonValue.Create(a.MSIFile),
        ["GUID"]        = JsonValue.Create(a.GUID),
        ["Dependencies"] = new JsonArray(a.Dependencies.Select(d => JsonValue.Create(d)).ToArray<JsonNode>()),
        ["DetectRunning"] = new JsonArray(a.DetectRunning.Select(d => (JsonNode)new JsonObject
        {
            ["DisplayName"] = JsonValue.Create(d.DisplayName),
            ["ExeFileName"] = JsonValue.Create(d.ExeFileName),
            ["Process"]     = JsonValue.Create(d.Process)
        }).ToArray()),
        ["URL"]        = JsonValue.Create(a.URL),
        ["DotVersion"] = JsonValue.Create(a.DotVersion),
        ["Version"]    = JsonValue.Create(a.Version),
        ["IconFile"]   = JsonValue.Create(a.IconFile),
        ["IconUserChosen"] = JsonValue.Create(a.IconUserChosen)
    };

    private static JsonObject BuildDetectObject(DetectSection d)
    {
        var obj = new JsonObject
        {
            ["Expression_Default"] = JsonValue.Create(d.Expression_Default)
        };

        // Additional named expressions (Expression_Project, Expression_Visio, etc.)
        foreach (var expr in d.Expressions)
        {
            if (!string.IsNullOrWhiteSpace(expr.Key))
                obj[$"Expression_{expr.Key}"] = JsonValue.Create(expr.Expression);
        }

        obj["Tests"] = new JsonArray(d.Tests.Select(t => (JsonNode)new JsonObject
        {
            ["Name"]     = JsonValue.Create(t.Name),
            ["Symbol"]   = JsonValue.Create(t.Symbol),
            ["Path"]     = JsonValue.Create(t.Path),
            ["Command"]  = JsonValue.Create(t.Command),
            ["Property"] = JsonValue.Create(t.Property),
            ["Operator"] = JsonValue.Create(t.Operator),
            ["Value"]    = JsonValue.Create(t.Value)
        }).ToArray());

        return obj;
    }

    private static JsonObject SerializeScript(ScriptSection s, string appName, string appVersion)
    {
        // Use AppName_Version_Section as fallback when Tag fields are empty
        var baseTag = string.IsNullOrWhiteSpace(appName) ? appVersion : $"{appName}_{appVersion}";
        string Tag(string t, string section) => string.IsNullOrWhiteSpace(t) ? $"{baseTag}_{section}" : t;

        var obj = new JsonObject
        {
            ["Comment"] = JsonValue.Create(s.Comment),
            ["Detect"] = BuildDetectObject(s.Detect),
            ["Install"] = new JsonObject
            {
                ["Tag"]             = JsonValue.Create(Tag(s.Install.Tag, "Install")),
                ["UninstallFirst"]  = JsonValue.Create(s.Install.UninstallFirst),
                ["RunAsAdmin"]      = JsonValue.Create(s.Install.RunAsAdmin),
                ["DetectApp"]       = JsonValue.Create(s.Install.DetectApp),
                ["CloseRunning"]    = JsonValue.Create(s.Install.CloseRunning),
                ["BackgroundColor"] = JsonValue.Create(s.Install.BackgroundColor),
                ["ForegroundColor"] = JsonValue.Create(s.Install.ForegroundColor)
            },
            ["Uninstall"] = new JsonObject
            {
                ["Tag"]             = JsonValue.Create(Tag(s.Uninstall.Tag, "Uninstall")),
                ["RunAsAdmin"]      = JsonValue.Create(s.Uninstall.RunAsAdmin),
                ["DetectApp"]       = JsonValue.Create(s.Uninstall.DetectApp),
                ["CloseRunning"]    = JsonValue.Create(s.Uninstall.CloseRunning),
                ["BackgroundColor"] = JsonValue.Create(s.Uninstall.BackgroundColor),
                ["ForegroundColor"] = JsonValue.Create(s.Uninstall.ForegroundColor)
            },
            ["Console"] = new JsonObject
            {
                ["Tag"] = JsonValue.Create(Tag(s.Console.Tag, "Console"))
            },
            ["IntunePackager"] = SerializeIntunePackager(s.IntunePackager, $"{baseTag}_IntunePackager"),
            ["SCCMPackager"]   = SerializeSCCMPackager(s.SCCMPackager, $"{baseTag}_SCCMPackager")
        };
        return obj;
    }

    private static JsonObject SerializeIntunePackager(IntunePackagerSection s, string defaultTag) => new()
    {
        ["Tag"]                  = JsonValue.Create(string.IsNullOrWhiteSpace(s.Tag) ? defaultTag : s.Tag),
        ["BackgroundColor"]      = JsonValue.Create(s.BackgroundColor),
        ["ForegroundColor"]      = JsonValue.Create(s.ForegroundColor),
        ["TerminateOnCollision"] = JsonValue.Create(s.TerminateOnCollision),
        ["Categories"]           = new JsonArray(s.Categories.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
        ["Packages"]             = new JsonArray(s.Packages.Select(p => (JsonNode)SerializeIntunePackage(p)).ToArray())
    };

    private static JsonObject SerializeIntunePackage(IntunePackageEntry p) => new()
    {
        ["AppName"]                          = JsonValue.Create(p.AppName),
        ["PackageId"]                        = JsonValue.Create(p.PackageId),
        ["IsEnabled"]                        = JsonValue.Create(p.IsEnabled),
        ["Comment"]                          = JsonValue.Create(p.Comment),
        ["Notes"]                            = JsonValue.Create(p.Notes),
        ["AppVersion"]                       = JsonValue.Create(p.AppVersion),
        ["IconFile"]                         = JsonValue.Create(p.IconFile),
        ["PackageOption"]                    = JsonValue.Create(p.PackageOption),
        ["UpdateMode"]                       = JsonValue.Create(p.UpdateMode.ToString()),
        ["ExistingAppID"]                    = JsonValue.Create(p.ExistingAppID),
        ["InstallCommand"]                   = JsonValue.Create(p.InstallCommand),
        ["UninstallCommand"]                 = JsonValue.Create(p.UninstallCommand),
        ["InstallExperience"]                = JsonValue.Create(p.InstallExperience),
        ["RestartBehavior"]                  = JsonValue.Create(p.RestartBehavior),
        ["MaximumInstallationTimeInMinutes"] = JsonValue.Create(p.MaximumInstallationTimeInMinutes),
        ["AllowAvailableUninstall"]          = JsonValue.Create(p.AllowAvailableUninstall),
        ["CompanyPortalFeaturedApp"]         = JsonValue.Create(p.CompanyPortalFeaturedApp),
        ["Developer"]                        = JsonValue.Create(p.Developer),
        ["Owner"]                            = JsonValue.Create(p.Owner),
        ["InformationURL"]                   = JsonValue.Create(p.InformationURL),
        ["PrivacyURL"]                       = JsonValue.Create(p.PrivacyURL),
        ["UseAzCopy"]                        = JsonValue.Create(p.UseAzCopy),
        ["AzCopyWindowStyle"]                = JsonValue.Create(p.AzCopyWindowStyle),
        ["Architecture"]                     = JsonValue.Create(p.Architecture),
        ["MinimumSupportedWindowsRelease"]   = JsonValue.Create(p.MinimumSupportedWindowsRelease),
        ["Categories"]             = new JsonArray(p.Categories.Select(c => (JsonNode)JsonValue.Create(c.Name)).ToArray()),
        ["ScopeTags"]              = new JsonArray(p.ScopeTags.Select(t => (JsonNode)JsonValue.Create(t.Name)).ToArray()),
        ["DetectionRules"]         = ParseRawArray(p.DetectionRules.Select(r => r.RawJson)),
        ["AdditionalRequirementRules"] = ParseRawArray(p.AdditionalRequirementRules.Select(r => r.RawJson)),
        ["CustomReturnCodes"]      = new JsonArray(p.CustomReturnCodes.Select(r => (JsonNode)new JsonObject
        {
            ["ReturnCode"] = JsonValue.Create(r.ReturnCode),
            ["Type"]       = JsonValue.Create(r.Type)
        }).ToArray()),
        ["Dependencies"]  = new JsonArray(p.Dependencies.Select(d => (JsonNode)new JsonObject
        {
            ["AppName"]     = JsonValue.Create(d.AppName),
            ["AutoInstall"] = JsonValue.Create(d.AutoInstall)
        }).ToArray()),
        ["Supersedence"]  = new JsonArray(p.Supersedence.Select(s => (JsonNode)new JsonObject
        {
            ["AppName"]          = JsonValue.Create(s.AppName),
            ["SupersedenceType"] = JsonValue.Create(s.SupersedenceType),
            ["UninstallOldApp"]  = JsonValue.Create(s.UninstallOldApp)
        }).ToArray()),
        // Single-tenant targeting
        ["TenantId"]  = JsonValue.Create(p.TenantId),
        // Package-level assignments
        ["Assignments"] = new JsonArray(p.Assignments.Select(a => (JsonNode)SerializeAssignment(a)).ToArray())
    };

    private static JsonObject SerializeSCCMPackager(SCCMPackagerSection s, string defaultTag) => new()
    {
        ["Tag"]      = JsonValue.Create(string.IsNullOrWhiteSpace(s.Tag) ? defaultTag : s.Tag),
        ["Packages"] = new JsonArray(s.Packages.Select(p => (JsonNode)new JsonObject
        {
            // Identity
            ["AppName"]                   = JsonValue.Create(p.AppName),
            ["PackageId"]                 = JsonValue.Create(p.PackageId),
            ["IsEnabled"]                 = JsonValue.Create(p.IsEnabled),
            ["AppComment"]                = JsonValue.Create(p.AppComment),
            ["Icon"]                      = JsonValue.Create(p.Icon),
            // New-CMApplication metadata
            ["Publisher"]                 = JsonValue.Create(p.Publisher),
            ["SoftwareVersion"]           = JsonValue.Create(p.SoftwareVersion),
            ["Owner"]                     = JsonValue.Create(p.Owner),
            ["SupportContact"]            = JsonValue.Create(p.SupportContact),
            ["Description"]               = JsonValue.Create(p.Description),
            ["ReleaseDate"]               = JsonValue.Create(p.ReleaseDate),
            ["LocalizedName"]             = JsonValue.Create(p.LocalizedName),
            ["LocalizedDescription"]      = JsonValue.Create(p.LocalizedDescription),
            ["Keywords"]                  = JsonValue.Create(p.Keywords),
            ["IsFeatured"]                = JsonValue.Create(p.IsFeatured),
            ["AutoInstall"]               = JsonValue.Create(p.AutoInstall),
            ["PrivacyUrl"]                = JsonValue.Create(p.PrivacyUrl),
            ["UserDocumentation"]         = JsonValue.Create(p.UserDocumentation),
            ["LinkText"]                  = JsonValue.Create(p.LinkText),
            // Add-CMScriptDeploymentType
            ["Name"]                      = JsonValue.Create(p.Name),
            ["Comment"]                   = JsonValue.Create(p.Comment),
            ["PackageOption"]             = JsonValue.Create(p.PackageOption),
            ["InstallCommand"]            = JsonValue.Create(p.InstallCommand),
            ["UninstallCommand"]          = JsonValue.Create(p.UninstallCommand),
            ["RepairCommand"]             = JsonValue.Create(p.RepairCommand),
            ["InstallationBehaviorType"]  = JsonValue.Create(p.InstallationBehaviorType),
            ["LogonRequirementType"]      = JsonValue.Create(p.LogonRequirementType),
            ["InstallBehavior"]           = JsonValue.Create(p.InstallBehavior),
            ["UserInteractionMode"]       = JsonValue.Create(p.UserInteractionMode),
            ["RebootBehavior"]            = JsonValue.Create(p.RebootBehavior),
            ["EstimatedRuntimeMins"]      = JsonValue.Create(p.EstimatedRuntimeMins),
            ["MaximumAllowedRuntimeMins"] = JsonValue.Create(p.MaximumAllowedRuntimeMins),
            ["SlowNetworkDeploymentMode"] = JsonValue.Create(p.SlowNetworkDeploymentMode),
            ["ContentFallback"]           = JsonValue.Create(p.ContentFallback),
            // Install behaviors
            ["InstallBehaviors"] = new JsonArray(p.InstallBehaviors.Select(ib => (JsonNode)new JsonObject
            {
                ["ExeFileName"] = JsonValue.Create(ib.ExeFileName),
                ["DisplayName"] = JsonValue.Create(ib.DisplayName)
            }).ToArray()),
            // Relationships
            ["Dependencies"]  = new JsonArray(p.Dependencies.Select(d => (JsonNode)new JsonObject
            {
                ["AppName"]     = JsonValue.Create(d.AppName),
                ["AutoInstall"] = JsonValue.Create(d.AutoInstall)
            }).ToArray()),
            ["Supersedence"]  = new JsonArray(p.Supersedence.Select(ss => (JsonNode)new JsonObject
            {
                ["AppName"]          = JsonValue.Create(ss.AppName),
                ["SupersedenceType"] = JsonValue.Create(ss.SupersedenceType)
            }).ToArray()),
            // Single-site targeting
            ["SiteCode"]  = JsonValue.Create(p.SiteCode),
            // Package-level deployments
            ["Deployments"] = new JsonArray(p.Deployments.Select(d => (JsonNode)SerializeSCCMDeployment(d)).ToArray())
        }).ToArray())
    };

    private static JsonObject SerializeSCCMSite(SCCMSiteEntry s) => new()
    {
        ["Comment"]          = JsonValue.Create(s.Comment),
        ["AppFolder"]        = JsonValue.Create(s.AppFolder),
        ["DeploymentGroups"] = new JsonArray(s.DeploymentGroups.Select(g => (JsonNode)JsonValue.Create(g)).ToArray()),
        ["IconFolder"]       = JsonValue.Create(s.IconFolder)
    };

    private static JsonObject SerializeSCCMDeployment(SCCMDeploymentEntry d) => new()
    {
        ["Label"]                      = JsonValue.Create(d.Label),
        ["Comment"]                    = JsonValue.Create(d.Comment),
        ["AppName"]                    = JsonValue.Create(d.AppName),
        ["PackageId"]                  = JsonValue.Create(d.PackageId),
        ["Collection"]                 = JsonValue.Create(d.Collection),
        ["UserNotification"]           = JsonValue.Create(d.UserNotification),
        ["DeployAction"]               = JsonValue.Create(d.DeployAction),
        ["DeployPurpose"]              = JsonValue.Create(d.DeployPurpose),
        ["AvailableDateTime"]          = JsonValue.Create(d.AvailableDateTime),
        ["DeadlineDateTime"]           = JsonValue.Create(d.DeadlineDateTime),
        ["TimeBaseOn"]                 = JsonValue.Create(d.TimeBaseOn),
        ["ApprovalRequired"]           = JsonValue.Create(d.ApprovalRequired),
        ["OverrideServiceWindow"]      = JsonValue.Create(d.OverrideServiceWindow),
        ["RebootOutsideServiceWindow"] = JsonValue.Create(d.RebootOutsideServiceWindow),
        ["SendWakeupPacket"]           = JsonValue.Create(d.SendWakeupPacket)
    };

    /// <summary>
    /// Sentinel written to Config.json in place of a plaintext ClientSecret.
    /// On load, <see cref="ParseIntuneTenant"/> treats this as empty so that
    /// <c>SettingsViewModel.EnrichTenantsFromSettings</c> rehydrates the
    /// real value from the DPAPI-encrypted settings.json.
    /// </summary>
    public const string ClientSecretSentinel = "ref:settings";

    private static JsonObject SerializeIntuneTenant(IntuneTenantEntry t)
    {
        // Never write a plaintext ClientSecret to Config.json. Config.json lives
        // inside the bundle directory and is git-committed; plaintext secrets
        // here end up permanently in git history on any subsequent push. Write
        // a sentinel; the real value stays DPAPI-encrypted in settings.json.
        //
        // Phase 15 (S-6): t.ClientSecret is now a SecureString; check Length
        // rather than IsNullOrEmpty. Note the sentinel is emitted even when
        // the entry only has a cipher (no fresh value typed) so consumers can
        // distinguish "has secret but lives elsewhere" from "no secret at all".
        var secretForDisk = (t.ClientSecret is { Length: > 0 } || !string.IsNullOrEmpty(t.ClientSecretCipher))
            ? ClientSecretSentinel
            : string.Empty;

        var obj = new JsonObject
        {
            ["Name"]                          = JsonValue.Create(t.Name),
            ["Comment"]                       = JsonValue.Create(t.Comment),
            ["Domain"]                        = JsonValue.Create(t.Domain),
            ["ClientID"]                      = JsonValue.Create(t.ClientID),
            ["AuthFlow"]                      = JsonValue.Create(t.AuthFlow.ToString()),
            ["ClientSecret"]                  = JsonValue.Create(secretForDisk),
            ["CertThumbprint"]                = JsonValue.Create(t.CertThumbprint),
            ["Architecture"]                  = JsonValue.Create(t.Architecture),
            ["MinimumSupportedWindowsRelease"] = JsonValue.Create(t.MinimumSupportedWindowsRelease),
            ["IntuneWinPath"]                 = JsonValue.Create(t.IntuneWinPath),
            ["IconFolder"]                    = JsonValue.Create(t.IconFolder),
            ["ScopeTags"]                     = new JsonArray(t.ScopeTags.Select(s => (JsonNode)JsonValue.Create(s)).ToArray())
        };
        return obj;
    }

    private static JsonObject SerializeAssignment(AssignmentEntry a) => new()
    {
        ["Label"]                               = JsonValue.Create(a.Label),
        ["AppName"]                             = JsonValue.Create(a.AppName),
        ["PackageId"]                           = JsonValue.Create(a.PackageId),
        ["Type"]                                = JsonValue.Create(a.Type),
        ["GroupID"]                             = JsonValue.Create(a.GroupID),
        ["GroupMode"]                           = JsonValue.Create(a.GroupMode),
        ["Intent"]                              = JsonValue.Create(a.Intent),
        ["Notification"]                        = JsonValue.Create(a.Notification),
        ["DeliveryOptimizationPriority"]        = JsonValue.Create(a.DeliveryOptimizationPriority),
        ["AvailableTime"]                       = JsonValue.Create(a.AvailableTime),
        ["DeadlineTime"]                        = JsonValue.Create(a.DeadlineTime),
        ["UseLocalTime"]                        = JsonValue.Create(a.UseLocalTime),
        ["AutoUpdateSupersededApps"]            = JsonValue.Create(a.AutoUpdateSupersededApps),
        ["EnableRestartGracePeriod"]            = JsonValue.Create(a.EnableRestartGracePeriod),
        ["RestartGracePeriodInMinutes"]         = JsonValue.Create(a.RestartGracePeriodInMinutes),
        ["RestartCountDownDisplayInMinutes"]    = JsonValue.Create(a.RestartCountDownDisplayInMinutes),
        ["RestartNotificationSnoozeInMinutes"]  = JsonValue.Create(a.RestartNotificationSnoozeInMinutes),
        ["FilterName"]                          = JsonValue.Create(a.FilterName),
        ["FilterMode"]                          = JsonValue.Create(a.FilterMode)
    };

    private static JsonObject SerializeDomain(DomainEntry d) => new()
    {
        ["isDistPath"] = JsonValue.Create(d.IsDistPath),
        ["AppFolder"]  = JsonValue.Create(d.AppFolder),
        ["TagFolder"]  = JsonValue.Create(d.TagFolder)
    };

    // -----------------------------------------------------------------------
    // Domain-specific helpers (general-purpose primitives moved to
    // Wrapp.Helpers.JsonObjectExtensions)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Normalises a ClientSecret value read from Config.json. If the stored value
    /// is the sentinel <see cref="ClientSecretSentinel"/>, returns empty so that
    /// the downstream <c>EnrichTenantsFromSettings</c> merge populates it from
    /// the DPAPI-encrypted settings.json. Legacy plaintext values pass through
    /// untouched so that existing bundles saved by older builds still open.
}
