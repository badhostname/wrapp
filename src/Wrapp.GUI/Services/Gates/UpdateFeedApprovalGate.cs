using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Advisory gate: an update feed URL is configured but not
/// trusted on this machine - typically right after org defaults seeded
/// <see cref="AppSettings.UpdateFeedUrl"/> into a fresh profile, or after the
/// URL changed. The feed delivers executable code, so Wrapp never contacts an
/// unapproved URL; until resolved, update checks return FeedNotApproved and
/// the status bar shows the action-needed indicator. Mirrors
/// <see cref="VaultUrlApprovalGate"/> (DPAPI trust-token pattern).
/// </summary>
public sealed class UpdateFeedApprovalGate : IAppGate
{
    public string Id => "update-feed-approval";
    public GateKind Kind => GateKind.Advisory;
    public string Title => "Approve update feed URL";

    public bool IsPending(AppSettings settings)
        => !string.IsNullOrEmpty(settings.UpdateFeedUrl)
           && !UpdateService.IsFeedTrusted(settings.UpdateFeedUrl, settings.UpdateFeedTrustToken);

    public async Task<bool> ResolveAsync(AppSettings settings)
    {
        var approved = await FluentDialog.ConfirmAsync(
            "Approve update feed",
            $"Wrapp will check for and download application updates from:\n\n    {settings.UpdateFeedUrl}\n\n" +
            "Updates are executable code - only approve a feed operated by your organization " +
            "(or the official Wrapp releases feed). If you did not set this, cancel and verify " +
            "settings.json / defaults.local.json were not edited by another tool or process.\n\n" +
            "Approve this feed for updates on this machine?",
            "Approve", "Cancel");
        if (!approved) return false;

        settings.UpdateFeedTrustToken = UpdateService.IssueFeedTrustToken(settings.UpdateFeedUrl);
        AppLogger.Info("Gate 'update-feed-approval': issued a new DPAPI trust token for the update feed URL");
        return true;
    }
}
