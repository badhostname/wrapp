using Wrapp.Models;

namespace Wrapp.Services.Targets;

/// <summary>
/// <see cref="IPublishTarget"/> for Microsoft Intune. Thin wrapper over the
/// Intune methods on <see cref="AppInventoryService"/>.
/// </summary>
public sealed class IntunePublishTarget : IPublishTarget
{
    private readonly AppInventoryService _inventory;

    public IntunePublishTarget(AppInventoryService inventory) => _inventory = inventory;

    public AppPlatform Kind => AppPlatform.Intune;
    public string DisplayName => "Intune";

    public TargetCapabilities Capabilities =>
        TargetCapabilities.ContentDownload
        | TargetCapabilities.GroupResolution
        | TargetCapabilities.ReturnCodes
        | TargetCapabilities.ScopeTags
        | TargetCapabilities.RequirementRules
        | TargetCapabilities.PerPackageDetectionRules
        | TargetCapabilities.Categories
        | TargetCapabilities.Assignments
        | TargetCapabilities.TokenAuth;

    public async Task<IReadOnlyList<object>> GetAppsAsync(string environmentKey, bool forceRefresh = false)
        => await _inventory.GetIntuneAppsAsync(environmentKey, forceRefresh);

    public Task<AppInventoryDetail?> GetAppDetailAsync(string environmentKey, string appIdentifier)
        => _inventory.GetIntuneAppDetailAsync(environmentKey, appIdentifier);
}
