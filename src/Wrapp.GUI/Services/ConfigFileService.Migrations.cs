using System.Text.Json.Nodes;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

public static partial class ConfigFileService
{
    // -----------------------------------------------------------------------
    // Post-load migrations -- legacy Config.json formats are upgraded in-place
    // so older bundles keep loading without a manual conversion step.
    // -----------------------------------------------------------------------

    private static void MigrateOldAssignmentsToPackages(JsonObject root, AppConfigModel model)
    {
        if (root["IntuneTenant"] is not JsonObject intuneTenants) return;

        foreach (var kv in intuneTenants)
        {
            if (kv.Key == "Comment" || kv.Value is not JsonObject tenantObj) continue;
            if (tenantObj["Assignments"] is not JsonArray assignments || assignments.Count == 0) continue;

            var tenantKey = kv.Key;

            foreach (var aNode in assignments)
            {
                if (aNode is not JsonObject aObj) continue;
                var entry = ParseAssignment(aObj);

                var pkg = model.Script.IntunePackager.Packages.FirstOrDefault(p =>
                    (!string.IsNullOrEmpty(entry.PackageId) && p.PackageId == entry.PackageId)
                    || (!string.IsNullOrEmpty(entry.AppName) && p.AppName == entry.AppName));

                if (pkg is null)
                {
                    AppLogger.Warn($"Migration: orphaned assignment '{entry.AppName}' on tenant {tenantKey} -- no matching package");
                    continue;
                }

                if (string.IsNullOrEmpty(pkg.TenantId))
                    pkg.TenantId = tenantKey;

                if (!string.Equals(pkg.TenantId, tenantKey, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn($"Migration: skipped assignment '{entry.AppName}' from tenant {tenantKey} (package targets {pkg.TenantId})");
                    continue;
                }

                if (string.IsNullOrEmpty(entry.PackageId))
                    entry.PackageId = pkg.PackageId;

                if (!pkg.Assignments.Any(a => a.PackageId == entry.PackageId && a.GroupID == entry.GroupID && a.Intent == entry.Intent))
                    pkg.Assignments.Add(entry);
            }
        }
    }

    /// <summary>
    /// Migrates old-format deployments (stored on SCCMSite entries) to packages.
    /// Old format: SCCMSite.{siteCode}.Deployments[]
    /// New format: Script.SCCMPackager.Packages[].Deployments[]
    /// </summary>
    private static void MigrateOldDeploymentsToPackages(JsonObject root, AppConfigModel model)
    {
        if (root["SCCMSite"] is not JsonObject sccmSites) return;

        foreach (var kv in sccmSites)
        {
            if (kv.Key == "Comment" || kv.Value is not JsonObject siteObj) continue;
            if (siteObj["Deployments"] is not JsonArray deployments || deployments.Count == 0) continue;

            var siteCode = kv.Key;

            foreach (var dNode in deployments)
            {
                if (dNode is not JsonObject dObj) continue;
                var entry = ParseSCCMDeployment(dObj);

                var pkg = model.Script.SCCMPackager.Packages.FirstOrDefault(p =>
                    (!string.IsNullOrEmpty(entry.PackageId) && p.PackageId == entry.PackageId)
                    || (!string.IsNullOrEmpty(entry.AppName) && p.AppName == entry.AppName));

                if (pkg is null)
                {
                    AppLogger.Warn($"Migration: orphaned deployment '{entry.AppName}' on site {siteCode} -- no matching package");
                    continue;
                }

                if (string.IsNullOrEmpty(pkg.SiteCode))
                    pkg.SiteCode = siteCode;

                if (!string.Equals(pkg.SiteCode, siteCode, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn($"Migration: skipped deployment '{entry.AppName}' from site {siteCode} (package targets {pkg.SiteCode})");
                    continue;
                }

                if (string.IsNullOrEmpty(entry.PackageId))
                    entry.PackageId = pkg.PackageId;

                if (!pkg.Deployments.Any(d => d.PackageId == entry.PackageId && d.Collection == entry.Collection))
                    pkg.Deployments.Add(entry);
            }
        }
    }
}
