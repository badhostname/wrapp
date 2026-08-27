using System.ComponentModel;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Phase 16a. Production <see cref="IFeatureGate"/> backed by
/// <see cref="AppSettings"/>. Each switch in <see cref="IsEnabled"/> maps a
/// <see cref="WrappFeatures"/> constant to the combination of settings that
/// actually has to be present for the feature to function -- adding a new
/// gate is therefore a one-line case plus a matching <see cref="DescribeWhyDisabled"/>
/// entry.
/// <para>
/// When <paramref name="settings"/> happens to implement
/// <see cref="INotifyPropertyChanged"/> the service auto-rebroadcasts on
/// relevant property changes. <see cref="AppSettings"/> is a plain POCO
/// today; callers that mutate the POCO directly should invoke
/// <see cref="NotifyChanged"/> after the mutation so the UI re-queries.
/// </para>
/// </summary>
public sealed class FeatureGateService : IFeatureGate
{
    private readonly AppSettings _settings;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FeatureGateService(AppSettings settings)
    {
        _settings = settings;
        if (_settings is INotifyPropertyChanged inpc)
            inpc.PropertyChanged += OnSettingsPropertyChanged;
    }

    public bool IsEnabled(string featureKey) => featureKey switch
    {
        WrappFeatures.AzureDevOpsKeyVault
            => _settings.EnableAzureDevOpsKeyVault
               && !string.IsNullOrEmpty(_settings.KeyVaultRepoUrl),
        _ => false,
    };

    public string? DescribeWhyDisabled(string featureKey) => featureKey switch
    {
        WrappFeatures.AzureDevOpsKeyVault when !_settings.EnableAzureDevOpsKeyVault
            => "Azure DevOps key vault is disabled in Settings.",
        WrappFeatures.AzureDevOpsKeyVault when string.IsNullOrEmpty(_settings.KeyVaultRepoUrl)
            => "Azure DevOps key vault URL is not configured.",
        WrappFeatures.AzureDevOpsKeyVault
            => null,
        _ => $"Unknown feature key: {featureKey}",
    };

    public void NotifyChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FeatureGate"));

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only rebroadcast for properties any gate actually reads. Adding a
        // new gate that consults more of AppSettings requires extending this
        // filter so the gate stays responsive.
        if (e.PropertyName is nameof(AppSettings.EnableAzureDevOpsKeyVault)
                            or nameof(AppSettings.KeyVaultRepoUrl))
        {
            NotifyChanged();
        }
    }
}
