using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Advisory gate raised by <see cref="Policy.PolicyChangeMonitor"/>: the
/// registry policy changed after launch, and this session still runs the
/// LAUNCH snapshot (restart-to-apply is the policy contract). Surfaces
/// through the same status-bar action-required indicator as every other
/// pending action. Resolving offers a restart: a NEW instance (which reads
/// the new policy) is started and this window closes through the normal
/// close guard — unsaved work still prompts, and cancelling simply leaves
/// both windows running, which multi-instance Wrapp fully supports.
/// </summary>
public sealed class PolicyChangedGate : IAppGate
{
    public string Id => "policy-changed";
    public GateKind Kind => GateKind.Advisory;
    public string Title => "Restart to apply updated organization policy";

    public bool IsPending(AppSettings settings) => Policy.PolicyService.ChangedSinceLaunch;

    public async Task<bool> ResolveAsync(AppSettings settings)
    {
        var restart = await FluentDialog.ConfirmAsync(
            "Organization policy changed",
            "Your organization's policy for Wrapp changed after this session started.\n\n" +
            "Wrapp reads policy once at launch, so this window is still running the " +
            "previous policy. Restart to apply the new one — a fresh window opens " +
            "first, and this one closes with the usual save prompts.",
            "Restart Wrapp", "Later");
        if (!restart) return false;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            AppLogger.Warn("Gate 'policy-changed': cannot determine process path for restart");
            return false;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
        });
        AppLogger.Info("Gate 'policy-changed': new instance started; closing this window");
        System.Windows.Application.Current?.MainWindow?.Close();
        return true;
    }
}
