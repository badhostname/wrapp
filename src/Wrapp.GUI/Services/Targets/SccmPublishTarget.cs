using Wrapp.Models;

namespace Wrapp.Services.Targets;

/// <summary>
/// <see cref="IPublishTarget"/> for Microsoft Configuration Manager (SCCM).
/// Thin wrapper over the SCCM methods on <see cref="AppInventoryService"/>.
/// SCCM runs local ConfigMgr cmdlets (no OAuth token) and uses opaque
/// collections rather than name-resolvable Azure AD groups.
/// </summary>
public sealed class SccmPublishTarget : IPublishTarget
{
    private readonly AppInventoryService _inventory;

    public SccmPublishTarget(AppInventoryService inventory) => _inventory = inventory;

    public AppPlatform Kind => AppPlatform.SCCM;
    public string DisplayName => "SCCM";

    public TargetCapabilities Capabilities =>
        TargetCapabilities.RepairCommand
        | TargetCapabilities.InstallBehaviors
        | TargetCapabilities.Deployments;

    public async Task<IReadOnlyList<object>> GetAppsAsync(string environmentKey, bool forceRefresh = false)
        => await _inventory.GetSccmAppsAsync(environmentKey, forceRefresh);

    public Task<AppInventoryDetail?> GetAppDetailAsync(string environmentKey, string appIdentifier)
        => _inventory.GetSccmAppDetailAsync(environmentKey, appIdentifier);
}
