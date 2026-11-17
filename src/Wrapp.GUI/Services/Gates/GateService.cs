using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Evaluates registered <see cref="IAppGate"/>s and surfaces them uniformly:
/// blocking gates are resolved via a sequential startup modal (declining shuts
/// the app down); advisory gates feed the status-bar "action needed" indicator
/// and are resolved on demand. Resolutions are persisted via the injected save
/// callback (defaults to <see cref="SettingsService.Save"/>; overridable so the
/// orchestration is unit-testable without touching disk).
/// </summary>
public sealed class GateService
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<IAppGate> _gates;
    private readonly Action<AppSettings> _save;

    public GateService(AppSettings settings, IEnumerable<IAppGate> gates, Action<AppSettings>? save = null)
    {
        _settings = settings;
        _gates = gates.ToList();
        _save = save ?? SettingsService.Save;
    }

    /// <summary>Advisory gates that are currently pending (drives the indicator).</summary>
    public IReadOnlyList<IAppGate> PendingAdvisory()
        => _gates.Where(g => g.Kind == GateKind.Advisory && g.IsPending(_settings)).ToList();

    /// <summary>
    /// Resolves every pending BLOCKING gate in registration order. Returns false
    /// as soon as the user declines one - the caller should shut the app down.
    /// Persists after each successful resolution.
    /// </summary>
    public async Task<bool> ResolveBlockingAsync()
    {
        foreach (var gate in _gates.Where(g => g.Kind == GateKind.Blocking))
        {
            if (!gate.IsPending(_settings)) continue;
            AppLogger.Info($"Gate '{gate.Id}': blocking gate pending, prompting user");
            if (!await gate.ResolveAsync(_settings))
            {
                AppLogger.Warn($"Gate '{gate.Id}': user declined a required gate");
                return false;
            }
            _save(_settings);
            AppLogger.Info($"Gate '{gate.Id}': resolved");
        }
        return true;
    }

    /// <summary>
    /// Resolves a single (advisory) gate on demand. No-op returning true if it's
    /// no longer pending. Persists on success.
    /// </summary>
    public async Task<bool> ResolveAsync(IAppGate gate)
    {
        if (!gate.IsPending(_settings)) return true;
        var ok = await gate.ResolveAsync(_settings);
        if (ok)
        {
            _save(_settings);
            AppLogger.Info($"Gate '{gate.Id}': resolved");
        }
        return ok;
    }

    /// <summary>
    /// Records the current app version as the last run, enabling future
    /// version-triggered gates (migrations / waiver re-acceptance) to compare
    /// against <see cref="AppSettings.LastRunVersion"/>. Persists only on change.
    /// </summary>
    public void RecordRun(string currentVersion)
    {
        if (string.Equals(_settings.LastRunVersion, currentVersion, StringComparison.Ordinal))
            return;
        AppLogger.Info($"Gate framework: app version {(_settings.LastRunVersion.Length == 0 ? "(new install)" : _settings.LastRunVersion)} -> {currentVersion}");
        _settings.LastRunVersion = currentVersion;
        _save(_settings);
    }
}
