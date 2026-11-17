using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// The open run clean-up notices, kept in <see cref="AppSettings.RunFollowUps"/>
/// (per machine, never exported) until the technician marks them done. The
/// Run view adds; <see cref="Gates.RunFollowUpGate"/> shows and clears;
/// <see cref="Changed"/> re-lights the status bar's action-needed indicator.
/// </summary>
public static class RunFollowUpStore
{
    private static AppSettings? _settings;
    private static Action<AppSettings> _save = SettingsService.Save;

    /// <summary>Raised after a notice is added or the list is cleared.</summary>
    public static event Action? Changed;

    public static void Initialize(AppSettings settings, Action<AppSettings>? save = null)
    {
        _settings = settings;
        if (save is not null) _save = save;
    }

    public static IReadOnlyList<RunFollowUp> Open(AppSettings settings) => settings.RunFollowUps ?? new List<RunFollowUp>();

    public static void Add(RunFollowUp followUp)
    {
        if (_settings is null) return;
        _settings.RunFollowUps ??= new();
        _settings.RunFollowUps.Add(followUp);
        Persist();
        Changed?.Invoke();
    }

    /// <summary>Marks every open notice done.</summary>
    public static void Clear(AppSettings settings)
    {
        settings.RunFollowUps?.Clear();
        Changed?.Invoke();
    }

    private static void Persist()
    {
        try { _save(_settings!); }
        catch (Exception ex) { AppLogger.Warn($"Run follow-ups: could not save the list: {ex.Message}"); }
    }
}
