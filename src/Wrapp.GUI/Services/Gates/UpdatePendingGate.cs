using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Advisory gate: an update is
/// available. This is the ONLY way an update surfaces mid-session - the
/// action-needed indicator, never a dialog over live work. Resolving asks the
/// user; "Update now" downloads (progress in the jobs panel) and closes
/// through the normal CloseGuard pipeline with save prompts and Cancel
/// intact; "Later" keeps the indicator lit and the session untouched.
/// </summary>
public sealed class UpdatePendingGate : IAppGate
{
    public string Id => "update-pending";
    public GateKind Kind => GateKind.Advisory;
    public string Title => $"Install Wrapp update {UpdateService.PendingUpdateVersion}";

    public bool IsPending(AppSettings settings) => UpdateService.HasPendingUpdate;

    public async Task<bool> ResolveAsync(AppSettings settings)
    {
        var version = UpdateService.PendingUpdateVersion;
        if (version is null) return true;

        var go = await FluentDialog.ConfirmAsync(
            "Update Wrapp",
            $"Wrapp {version} is available (you are running {AppInfo.VersionDisplay}).\n\n" +
            "Update now? You'll get the normal save prompts, and any other open Wrapp windows " +
            "are asked to close with their own save prompts - open work always comes first. " +
            "The download starts only after that, on the update screen, and Wrapp restarts on " +
            "the new version. Choose Later to keep working - this stays in the actions list.",
            "Update now", "Later");
        if (!go) return false;   // stays pending; indicator stays lit

        // CloseGuard runs inside; cancelling a save prompt aborts the update
        // and the indicator stays lit.
        return await UpdateFlowController.BeginFromSessionAsync(settings, version);
    }
}
