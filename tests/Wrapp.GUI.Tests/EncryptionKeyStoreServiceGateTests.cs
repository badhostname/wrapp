using System.ComponentModel;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Phase 16b. Covers the silent-skip contract that
/// <see cref="EncryptionKeyStoreService"/> enforces when the
/// <see cref="WrappFeatures.AzureDevOpsKeyVault"/> gate is OFF: writes throw
/// <see cref="EncryptionKeyStoreService.DevOpsVaultNotConfiguredException"/>
/// with the gate's reason text, and reads silently return null / false / empty
/// without ever touching the HTTP layer.
/// <para>
/// The point of these tests is that the gate check fires <i>before</i> any
/// vault I/O so a gate-off setup behaves identically to a never-configured
/// vault from the caller's perspective -- no dialogs, no network errors, no
/// surprise stack traces.
/// </para>
/// </summary>
public class EncryptionKeyStoreServiceGateTests
{
    /// <summary>
    /// Test double that lets a test toggle the gate and assert that the
    /// service consults it before doing anything else.
    /// </summary>
    private sealed class StubGate : IFeatureGate
    {
        public bool Enabled { get; set; } = true;
        public string? Reason { get; set; }
        public int IsEnabledCallCount { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsEnabled(string featureKey)
        {
            IsEnabledCallCount++;
            return Enabled && featureKey == WrappFeatures.AzureDevOpsKeyVault;
        }

        public string? DescribeWhyDisabled(string featureKey) => Reason;

        public void NotifyChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FeatureGate"));
    }

    private static EncryptionKeyStoreService MakeService(StubGate gate, string url = "https://dev.azure.com/org/proj/_git/repo")
    {
        var settings = new AppSettings { KeyVaultRepoUrl = url };
        var devOps = new DevOpsAuthService();
        return new EncryptionKeyStoreService(devOps, settings, gate);
    }

    [Fact]
    public async Task SaveKeysAsync_GateOff_ThrowsDevOpsVaultNotConfigured_WithGateReason()
    {
        var gate = new StubGate { Enabled = false, Reason = "Azure DevOps key vault is disabled in Settings." };
        var service = MakeService(gate);
        var keys = new EncryptionKeyInfo { AppId = "abc", TenantId = "t1", EncryptionKey = "k" };

        var ex = await Assert.ThrowsAsync<EncryptionKeyStoreService.DevOpsVaultNotConfiguredException>(
            () => service.SaveKeysAsync(keys));
        Assert.Contains("disabled in Settings", ex.Message);
        Assert.Equal(1, gate.IsEnabledCallCount);
    }

    [Fact]
    public async Task GetKeysAsync_GateOff_ReturnsNull_WithoutHttpCall()
    {
        var gate = new StubGate { Enabled = false };
        var service = MakeService(gate);

        var result = await service.GetKeysAsync("tenant", "app");

        Assert.Null(result);
        Assert.Equal(1, gate.IsEnabledCallCount);
    }

    [Fact]
    public async Task HasKeysAsync_GateOff_ReturnsFalse_WithoutHttpCall()
    {
        var gate = new StubGate { Enabled = false };
        var service = MakeService(gate);

        var result = await service.HasKeysAsync("tenant", "app");

        Assert.False(result);
        Assert.Equal(1, gate.IsEnabledCallCount);
    }

    [Fact]
    public async Task LoadAllDevOpsKeysAsync_GateOff_ReturnsEmpty_WithoutHttpCall()
    {
        var gate = new StubGate { Enabled = false };
        var service = MakeService(gate);

        var result = await service.LoadAllDevOpsKeysAsync();

        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.Equal(1, gate.IsEnabledCallCount);
    }

    [Fact]
    public void DevOpsVaultNotConfiguredException_WithReason_QuotesReasonInMessage()
    {
        var ex = new EncryptionKeyStoreService.DevOpsVaultNotConfiguredException(
            "Azure DevOps key vault URL is not configured.");

        Assert.Contains("URL is not configured", ex.Message);
    }

    [Fact]
    public void DevOpsVaultNotConfiguredException_NullReason_FallsBackToDefaultMessage()
    {
        var ex = new EncryptionKeyStoreService.DevOpsVaultNotConfiguredException((string?)null);

        Assert.Contains("Configure it in Settings -> Key Vault", ex.Message);
    }
}
