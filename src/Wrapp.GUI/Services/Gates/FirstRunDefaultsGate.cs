using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Blocking first-run gate: lets the technician provision Wrapp with their
/// organization's defaults - tenants, SCCM sites, domains, key-vault URL,
/// update feed, endpoint folders and the preference blocks - by browsing to a
/// company defaults JSON file, or continue with the built-in examples.
/// <para>
/// Why here rather than in the installer: MSI custom dialogs would mean
/// forking Velopack's WiX template, and a per-machine install can't write a
/// per-user profile anyway. Running at first launch also means a technician
/// who was deployed the app silently (Intune/SCCM) still gets the choice, and
/// the file is copied to a location that SURVIVES UPDATES
/// (<see cref="DefaultsLoader.DurableDefaultsPath"/> - the Velopack install
/// root, not <c>current\</c>, which is replaced wholesale on every update).
/// </para>
/// <para>
/// Resolution is recorded in <see cref="AppSettings.GateState"/> so the prompt
/// appears exactly once per profile. A defaults file already present in any
/// <see cref="DefaultsLoader.CandidatePaths"/> location (org pre-provisioned
/// the machine) satisfies the gate silently - no prompt at all.
/// </para>
/// </summary>
public sealed class FirstRunDefaultsGate : IAppGate
{
    public string Id => "first-run-defaults";
    public GateKind Kind => GateKind.Blocking;
    public string Title => "Set up organization defaults";

    /// <summary>Injected so the gate can re-seed settings after a file is chosen.</summary>
    private readonly Action<OrgDefaults>? _applyDefaults;

    public FirstRunDefaultsGate(Action<OrgDefaults>? applyDefaults = null)
        => _applyDefaults = applyDefaults;

    public bool IsPending(AppSettings settings)
    {
        // Already answered on this profile.
        if (settings.GateState.ContainsKey(Id)) return false;
        // Org pre-provisioned the machine -- nothing to ask.
        if (DefaultsLoader.FindDefaultsFile() is not null) return false;
        return true;
    }

    public async Task<bool> ResolveAsync(AppSettings settings)
    {
        var useOrgFile = await FluentDialog.ConfirmAsync(
            "Set up Wrapp for your organization",
            "Wrapp can start pre-configured with your organization's settings - Intune tenants, " +
            "SCCM sites, domains and distribution paths, the Key Vault repo, the update feed, and " +
            "packaging defaults.\n\n" +
            "If your IT team gave you a defaults file (a .json), choose it now. Otherwise continue " +
            "with the built-in examples - you can configure everything later in Settings, or import " +
            "a defaults file from Settings at any time.\n\n" +
            "Note: secrets are never read from this file; sign-in still happens per user.",
            "Choose defaults file...", "Use examples");

        if (!useOrgFile)
        {
            settings.GateState[Id] = "examples";
            AppLogger.Info("FirstRunDefaults: technician chose the built-in examples");
            return true;
        }

        var picked = FileDialogService.BrowseFile(
            "JSON files (*.json)|*.json|All files (*.*)|*.*",
            "Select your organization's Wrapp defaults file");
        if (picked is null)
        {
            // Cancelled the picker -- treat as "not answered", re-prompt next
            // launch rather than silently locking in the examples.
            AppLogger.Info("FirstRunDefaults: file picker cancelled; will ask again next launch");
            return false;
        }

        if (!TryImport(picked, out var error))
        {
            await FluentDialog.ShowWarningAsync(
                "Defaults file not usable",
                $"{error}\n\nWrapp will continue with the built-in examples. You can import a " +
                "corrected file later from Settings.");
            settings.GateState[Id] = "examples-after-error";
            return true;
        }

        settings.GateState[Id] = "imported";
        AppLogger.Info($"FirstRunDefaults: imported organization defaults from {picked}");

        // Re-run seeding NOW so this launch is already provisioned: the normal
        // seed pass ran before this gate (it needs settings loaded), and the
        // one-shot flag would otherwise block it forever.
        settings.OrgDefaultsSeeded = false;
        _applyDefaults?.Invoke(DefaultsLoader.Load());
        return true;
    }

    /// <summary>
    /// Validates the chosen file parses as org defaults, then copies it to the
    /// durable location so it survives app updates. Internal for tests.
    /// </summary>
    internal static bool TryImport(string sourcePath, out string error)
    {
        error = string.Empty;
        try
        {
            var json = File.ReadAllText(sourcePath);
            var parsed = JsonSerializer.Deserialize<OrgDefaults>(json, JsonDefaults.CaseInsensitive);
            if (parsed is null)
            {
                error = "The file did not contain a defaults object.";
                return false;
            }
        }
        catch (JsonException ex)
        {
            error = $"The file is not valid JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"The file could not be read: {ex.Message}";
            return false;
        }

        try
        {
            var target = DefaultsLoader.DurableDefaultsPath();
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(sourcePath, target, overwrite: true);
            // The old file may already be cached (SettingsViewModel's
            // DefaultTenants/Sites/Domains read through the cache) - without
            // this, the import is invisible until the app restarts.
            DefaultsLoader.InvalidateCache();
            return true;
        }
        catch (Exception ex)
        {
            error = $"The file could not be saved to the Wrapp data folder: {ex.Message}";
            return false;
        }
    }
}
