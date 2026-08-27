using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Repairs settings.json values that are structurally invalid — the residue of
/// profiles written while the defaults POCOs had no initializers, where a
/// never-saved Preferences section persisted as empty strings and zeros.
/// <para>
/// Deliberately CONSERVATIVE: it only replaces values that cannot be a real
/// choice (an empty enum-like field, or a duration that must be positive).
/// Values a technician could legitimately have chosen — an empty deadline, an
/// empty filter, a cleared metadata template, a zero restart grace period —
/// are never touched.
/// </para>
/// <para>
/// This also keeps org provisioning working: <see cref="OrgDefaultsSeeder"/>
/// applies a block only while it is JSON-identical to <c>new AppSettings()</c>.
/// Repairing an all-zero block back to the seed values restores that equality,
/// so an organization defaults file still lands on a profile that was created
/// before this fix.
/// </para>
/// </summary>
public static class SettingsRepair
{
    /// <summary>Applies every repair. Returns true when something changed.</summary>
    public static bool Apply(AppSettings settings)
    {
        var changed = false;
        changed |= RepairNullContainers(settings);
        changed |= RepairIntunePackage(settings.IntunePackageDefaults);
        changed |= RepairIntuneAssignment(settings.IntuneAssignmentDefaults);
        changed |= RepairSccmPackage(settings.SccmPackageDefaults);
        changed |= RepairSccmDeployment(settings.SccmDeploymentDefaults);
        if (changed)
            AppLogger.Info("SettingsRepair: restored unset package/assignment defaults to their seed values");
        return changed;
    }

    /// <summary>
    /// SEC-3: a hand-edited (or tool-mangled) settings.json with an explicit
    /// <c>null</c> for a collection/defaults block deserializes cleanly, so the
    /// backup-recovery path never runs — the NRE then fires later in startup
    /// where nothing can recover it. Coalesce every container so a null key
    /// degrades to "empty" instead of bricking every launch.
    /// </summary>
    private static bool RepairNullContainers(AppSettings s)
    {
        var changed = false;
        bool Fix<T>(T? value, Action assign) where T : class
        {
            if (value is not null) return false;
            assign();
            return true;
        }
        changed |= Fix(s.IntuneTenants,    () => s.IntuneTenants = new());
        changed |= Fix(s.SccmSites,        () => s.SccmSites = new());
        changed |= Fix(s.Domains,          () => s.Domains = new());
        changed |= Fix(s.Placeholders,     () => s.Placeholders = new());
        changed |= Fix(s.TenantNameCache,  () => s.TenantNameCache = new());
        changed |= Fix(s.GateState,        () => s.GateState = new());
        changed |= Fix(s.IntunePackageDefaults,    () => s.IntunePackageDefaults = new());
        changed |= Fix(s.IntuneMetadataDefaults,   () => s.IntuneMetadataDefaults = new());
        changed |= Fix(s.IntuneAssignmentDefaults, () => s.IntuneAssignmentDefaults = new());
        changed |= Fix(s.SccmPackageDefaults,      () => s.SccmPackageDefaults = new());
        changed |= Fix(s.SccmMetadataDefaults,     () => s.SccmMetadataDefaults = new());
        changed |= Fix(s.SccmDeploymentDefaults,   () => s.SccmDeploymentDefaults = new());
        foreach (var site in s.SccmSites ?? Enumerable.Empty<SavedSiteEntry>())
        {
            if (site.DeploymentGroups is null) { site.DeploymentGroups = new(); changed = true; }
        }
        if (changed)
            AppLogger.Warn("SettingsRepair: settings.json contained null sections; reset to empty defaults");
        return changed;
    }

    private static bool RepairIntunePackage(IntunePackageDefaults p)
    {
        var changed = false;
        changed |= Fill(() => p.Architecture, v => p.Architecture = v, ModuleDefaultsSeed.IntuneArchitecture);
        changed |= Fill(() => p.MinimumSupportedWindowsRelease, v => p.MinimumSupportedWindowsRelease = v, ModuleDefaultsSeed.IntuneMinimumSupportedWindowsRelease);
        changed |= Fill(() => p.InstallExperience, v => p.InstallExperience = v, ModuleDefaultsSeed.IntuneInstallExperience);
        changed |= Fill(() => p.RestartBehavior, v => p.RestartBehavior = v, ModuleDefaultsSeed.IntuneRestartBehavior);
        changed |= Fill(() => p.AzCopyWindowStyle, v => p.AzCopyWindowStyle = v, ModuleDefaultsSeed.IntuneAzCopyWindowStyle);

        // Intune rejects anything outside 1-1440; 0 is the "never saved" residue.
        if (p.MaximumInstallationTimeInMinutes is < 1 or > 1440)
        {
            p.MaximumInstallationTimeInMinutes = ModuleDefaultsSeed.IntuneMaximumInstallationTimeInMinutes;
            changed = true;
        }
        return changed;
    }

    private static bool RepairIntuneAssignment(IntuneAssignmentDefaults a)
    {
        var changed = false;
        changed |= Fill(() => a.Intent, v => a.Intent = v, ModuleDefaultsSeed.IntuneAssignmentIntent);
        changed |= Fill(() => a.Type, v => a.Type = v, ModuleDefaultsSeed.IntuneAssignmentType);
        changed |= Fill(() => a.GroupMode, v => a.GroupMode = v, ModuleDefaultsSeed.IntuneAssignmentGroupMode);
        changed |= Fill(() => a.Notification, v => a.Notification = v, ModuleDefaultsSeed.IntuneAssignmentNotification);
        changed |= Fill(() => a.DeliveryOptimizationPriority, v => a.DeliveryOptimizationPriority = v, ModuleDefaultsSeed.IntuneAssignmentDeliveryOptimizationPriority);
        changed |= Fill(() => a.AvailableTime, v => a.AvailableTime = v, ModuleDefaultsSeed.IntuneAssignmentAvailableTime);
        // DeadlineTime / FilterMode: empty is a real choice, left alone.
        return changed;
    }

    private static bool RepairSccmPackage(SccmPackageDefaults s)
    {
        var changed = false;
        changed |= Fill(() => s.InstallationBehaviorType, v => s.InstallationBehaviorType = v, ModuleDefaultsSeed.SccmInstallationBehaviorType);
        changed |= Fill(() => s.LogonRequirementType, v => s.LogonRequirementType = v, ModuleDefaultsSeed.SccmLogonRequirementType);
        changed |= Fill(() => s.UserInteractionMode, v => s.UserInteractionMode = v, ModuleDefaultsSeed.SccmUserInteractionMode);
        changed |= Fill(() => s.RebootBehavior, v => s.RebootBehavior = v, ModuleDefaultsSeed.SccmRebootBehavior);
        changed |= Fill(() => s.SlowNetworkDeploymentMode, v => s.SlowNetworkDeploymentMode = v, ModuleDefaultsSeed.SccmSlowNetworkDeploymentMode);

        // A deployment type with a 0-minute runtime is meaningless to ConfigMgr.
        if (s.EstimatedRuntimeMins <= 0)
        {
            s.EstimatedRuntimeMins = ModuleDefaultsSeed.SccmEstimatedRuntimeMins;
            changed = true;
        }
        if (s.MaximumAllowedRuntimeMins <= 0)
        {
            s.MaximumAllowedRuntimeMins = ModuleDefaultsSeed.SccmMaximumAllowedRuntimeMins;
            changed = true;
        }
        return changed;
    }

    private static bool RepairSccmDeployment(SccmDeploymentDefaults d)
    {
        var changed = false;
        changed |= Fill(() => d.DeployAction, v => d.DeployAction = v, ModuleDefaultsSeed.SccmDeployAction);
        changed |= Fill(() => d.DeployPurpose, v => d.DeployPurpose = v, ModuleDefaultsSeed.SccmDeployPurpose);
        changed |= Fill(() => d.UserNotification, v => d.UserNotification = v, ModuleDefaultsSeed.SccmUserNotification);
        changed |= Fill(() => d.TimeBaseOn, v => d.TimeBaseOn = v, ModuleDefaultsSeed.SccmTimeBaseOn);
        changed |= Fill(() => d.AvailableDateTime, v => d.AvailableDateTime = v, ModuleDefaultsSeed.SccmAvailableDateTime);
        // DeadlineDateTime: empty = no deadline, left alone.
        return changed;
    }

    private static bool Fill(Func<string> get, Action<string> set, string seed)
    {
        if (!string.IsNullOrWhiteSpace(get())) return false;
        if (string.IsNullOrEmpty(seed)) return false;   // seed is itself "unset"
        set(seed);
        return true;
    }
}
