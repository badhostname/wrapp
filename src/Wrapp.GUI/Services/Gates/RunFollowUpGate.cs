using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Advisory gate: a packaging run failed part-way and left something to clean
/// up (an app half-created in Intune, an SCCM application without its
/// deployment type, an extraction folder). Lit while
/// <see cref="AppSettings.RunFollowUps"/> holds open notices, which outlive
/// the session. Resolving shows each notice - the step that failed, the
/// reason, the likely actions - and "Mark done" clears them. Wrapp never
/// removes anything in Intune or Configuration Manager itself.
/// </summary>
public sealed class RunFollowUpGate : IAppGate
{
    public string Id => "run-follow-ups";
    public GateKind Kind => GateKind.Advisory;

    // Title is read when the indicator refreshes, after IsPending.
    private int _count;
    public string Title => _count == 1
        ? "Clean up after a failed run"
        : $"Clean up after {_count} failed runs";

    public bool IsPending(AppSettings settings)
    {
        _count = RunFollowUpStore.Open(settings).Count;
        return _count > 0;
    }

    public async Task<bool> ResolveAsync(AppSettings settings)
    {
        var open = RunFollowUpStore.Open(settings).ToList();
        if (open.Count == 0) return true;

        var markdown = RunFollowUp.ToMarkdown(open);
        var source = System.Windows.Application.Current?.MainWindow;
        var done = source is not null
            ? await FluentDialog.ConfirmMarkdownAsync(Title, markdown, source, "Mark done", "Keep")
            : await FluentDialog.ShowSelectAsync(Title, markdown, "Mark done", "Keep");
        if (!done) return false;   // stays pending; indicator stays lit

        AppLogger.Info($"Gate '{Id}': {open.Count} run follow-up(s) marked done");
        RunFollowUpStore.Clear(settings);
        return true;
    }
}
