using System.ComponentModel;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase 16a. Covers the <see cref="FeatureGateService"/> resolution matrix
/// for the <see cref="WrappFeatures.AzureDevOpsKeyVault"/> gate, the
/// human-readable reasons surfaced by <see cref="IFeatureGate.DescribeWhyDisabled"/>,
/// and the explicit <see cref="IFeatureGate.NotifyChanged"/> rebroadcast that
/// lets bound UI re-query after a settings save mutates the underlying POCO.
/// </summary>
public class FeatureGateServiceTests
{
    [Fact]
    public void IsEnabled_VaultEnabled_WithUrl_ReturnsTrue()
    {
        var settings = new AppSettings
        {
            EnableAzureDevOpsKeyVault = true,
            KeyVaultRepoUrl = "https://dev.azure.com/org/project/_git/repo",
        };
        var gate = new FeatureGateService(settings);

        Assert.True(gate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault));
        Assert.Null(gate.DescribeWhyDisabled(WrappFeatures.AzureDevOpsKeyVault));
    }

    [Fact]
    public void IsEnabled_VaultDisabled_ReturnsFalse_RegardlessOfUrl()
    {
        var settings = new AppSettings
        {
            EnableAzureDevOpsKeyVault = false,
            KeyVaultRepoUrl = "https://dev.azure.com/org/project/_git/repo",
        };
        var gate = new FeatureGateService(settings);

        Assert.False(gate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault));
        Assert.Equal(
            "Azure DevOps key vault is disabled in Settings.",
            gate.DescribeWhyDisabled(WrappFeatures.AzureDevOpsKeyVault));
    }

    [Fact]
    public void IsEnabled_VaultEnabled_NoUrl_ReturnsFalse()
    {
        var settings = new AppSettings
        {
            EnableAzureDevOpsKeyVault = true,
            KeyVaultRepoUrl = string.Empty,
        };
        var gate = new FeatureGateService(settings);

        Assert.False(gate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault));
        Assert.Equal(
            "Azure DevOps key vault URL is not configured.",
            gate.DescribeWhyDisabled(WrappFeatures.AzureDevOpsKeyVault));
    }

    [Fact]
    public void IsEnabled_UnknownFeatureKey_ReturnsFalse_WithDiagnosticReason()
    {
        var gate = new FeatureGateService(new AppSettings());

        Assert.False(gate.IsEnabled("DoesNotExist"));
        Assert.Equal("Unknown feature key: DoesNotExist",
            gate.DescribeWhyDisabled("DoesNotExist"));
    }

    [Fact]
    public void NotifyChanged_FiresPropertyChanged_WithFeatureGateName()
    {
        var settings = new AppSettings();
        var gate = new FeatureGateService(settings);

        PropertyChangedEventArgs? captured = null;
        ((INotifyPropertyChanged)gate).PropertyChanged += (_, e) => captured = e;

        gate.NotifyChanged();

        Assert.NotNull(captured);
        Assert.Equal("FeatureGate", captured!.PropertyName);
    }

    [Fact]
    public void IsEnabled_ReflectsSettingsMutation_AfterNotifyChanged()
    {
        var settings = new AppSettings
        {
            EnableAzureDevOpsKeyVault = true,
            KeyVaultRepoUrl = "https://dev.azure.com/org/project/_git/repo",
        };
        var gate = new FeatureGateService(settings);
        Assert.True(gate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault));

        settings.EnableAzureDevOpsKeyVault = false;
        // No PropertyChanged on AppSettings (POCO), but IsEnabled re-queries
        // every call so the next observation already reflects the mutation.
        Assert.False(gate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault));

        // NotifyChanged is what bound UI elements consume; verify it fires
        // even when the POCO can't broadcast on its own.
        var fired = 0;
        ((INotifyPropertyChanged)gate).PropertyChanged += (_, _) => fired++;
        gate.NotifyChanged();
        Assert.Equal(1, fired);
    }
}
