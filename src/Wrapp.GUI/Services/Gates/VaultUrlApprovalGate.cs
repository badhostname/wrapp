using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Advisory gate: a Key Vault URL is configured but not trusted on this machine
/// — e.g. after the SEC-1 upgrade replaced the forgeable hash with a DPAPI trust
/// token (existing approvals no longer validate), or after the URL changed.
/// Surfaced in the status bar; resolving issues a fresh machine-bound trust
/// token after user confirmation. This is the first, canonical instance of the
/// gate framework and the discoverable replacement for the old
/// dirty-Save-to-approve friction.
/// </summary>
public sealed class VaultUrlApprovalGate : IAppGate
{
    public string Id => "vault-url-approval";
    public GateKind Kind => GateKind.Advisory;
    public string Title => "Approve Key Vault URL";

    public bool IsPending(AppSettings settings)
        => !string.IsNullOrEmpty(settings.KeyVaultRepoUrl)
           && !EncryptionKeyStoreService.IsKeyVaultUrlTrusted(settings.KeyVaultRepoUrl, settings.KeyVaultRepoUrlHash);

    public async Task<bool> ResolveAsync(AppSettings settings)
    {
        var approved = await FluentDialog.ConfirmAsync(
            "Approve Key Vault URL",
            $"Encryption keys will be pushed to / fetched from:\n\n    {settings.KeyVaultRepoUrl}\n\n" +
            "Confirm this is the correct Azure DevOps repo. If you did not set this, cancel and " +
            "verify settings.json was not edited by another tool or process.\n\n" +
            "Approve this URL for key operations on this machine?",
            "Approve", "Cancel");
        if (!approved) return false;

        settings.KeyVaultRepoUrlHash =
            EncryptionKeyStoreService.IssueKeyVaultTrustToken(settings.KeyVaultRepoUrl);
        AppLogger.Info("Gate 'vault-url-approval': issued a new DPAPI trust token for the Key Vault URL");
        return true;
    }
}
