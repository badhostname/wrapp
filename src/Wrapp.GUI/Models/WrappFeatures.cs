namespace Wrapp.Models;

/// <summary>
/// Canonical string identifiers for each optional feature gated by
/// <see cref="Services.IFeatureGate"/>. Phase 16a seeds the catalogue with
/// the Azure DevOps key vault; future "user can opt out of X" surfaces add
/// their own constant here, then teach <see cref="Services.FeatureGateService"/>
/// how to resolve it.
/// </summary>
public static class WrappFeatures
{
    /// <summary>Azure DevOps git-backed encryption key vault (read + write).</summary>
    public const string AzureDevOpsKeyVault = "AzureDevOpsKeyVault";
}
