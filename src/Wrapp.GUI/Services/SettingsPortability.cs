using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Export / import of a technician's full settings + preferences as a single
/// JSON file — for backing up a configured profile, moving to a new machine,
/// or handing an org-wide baseline to colleagues (the exported file is also a
/// valid organization defaults file for the first-run gate).
/// <para>
/// SECURITY: machine-bound and per-user secrets are STRIPPED on export —
/// DPAPI-protected client secrets, the Key Vault trust token, and the update
/// feed trust token. They are meaningless on another machine (DPAPI CurrentUser
/// can't decrypt them elsewhere) and exporting them would spread ciphertext
/// that only invites offline attack. Trust approvals are per-machine decisions
/// by design (SEC-1) and must be re-made after import.
/// </para>
/// </summary>
public static class SettingsPortability
{
    /// <summary>Property names never written to an exported file.</summary>
    internal static readonly string[] StrippedProperties =
    {
        "KeyVaultRepoUrlHash",    // DPAPI trust token (per-machine)
        "UpdateFeedTrustToken",   // DPAPI trust token (per-machine)
        "ClientSecret",           // DPAPI ciphertext on tenant entries
        "GateState",              // per-profile gate answers (waiver, first-run)
        "OrgDefaultsSeeded",      // one-shot marker; importing should re-seed
        "LastRunVersion",
        "LastSeenChangelogVersion",
    };

    /// <summary>
    /// Serializes <paramref name="settings"/> with the per-machine/secret
    /// properties removed, at any nesting depth.
    /// </summary>
    public static string BuildExportJson(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, JsonDefaults.Pretty) as JsonObject
                   ?? new JsonObject();
        Strip(node);
        return node.ToJsonString(JsonDefaults.Pretty);
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in StrippedProperties) obj.Remove(name);
                foreach (var kvp in obj) Strip(kvp.Value);
                break;
            case JsonArray arr:
                foreach (var item in arr) Strip(item);
                break;
        }
    }

    /// <summary>Writes the sanitized export to <paramref name="path"/>.</summary>
    public static bool TryExport(AppSettings settings, string path, out string error)
    {
        error = string.Empty;
        try
        {
            File.WriteAllText(path, BuildExportJson(settings));
            AppLogger.Info($"Settings exported to {path} (secrets and per-machine trust tokens stripped)");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.Warn($"Settings export failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Parses an exported settings file. Returns null on failure with
    /// <paramref name="error"/> set. Does NOT touch the live settings — the
    /// caller decides how to merge (see <see cref="ApplyImported"/>).
    /// </summary>
    public static AppSettings? TryParse(string path, out string error)
    {
        error = string.Empty;
        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.CaseInsensitive);
            if (parsed is null) { error = "The file did not contain a settings object."; return null; }
            return parsed;
        }
        catch (JsonException ex) { error = $"The file is not valid JSON: {ex.Message}"; return null; }
        catch (Exception ex) { error = $"The file could not be read: {ex.Message}"; return null; }
    }

    /// <summary>
    /// Copies imported values onto the LIVE settings instance (mutated in
    /// place so every binding holding a reference keeps working). Secrets,
    /// trust tokens and gate state on the target are preserved — an import
    /// can never grant trust or leak a secret.
    /// </summary>
    public static void ApplyImported(AppSettings target, AppSettings imported)
    {
        // Preserve the per-machine / per-profile values.
        var keepVaultToken = target.KeyVaultRepoUrlHash;
        var keepFeedToken = target.UpdateFeedTrustToken;
        var keepGateState = target.GateState;
        var keepLastRun = target.LastRunVersion;
        var keepLastSeen = target.LastSeenChangelogVersion;

        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            prop.SetValue(target, prop.GetValue(imported));
        }

        target.KeyVaultRepoUrlHash = keepVaultToken;
        target.UpdateFeedTrustToken = keepFeedToken;
        target.GateState = keepGateState;
        target.LastRunVersion = keepLastRun;
        target.LastSeenChangelogVersion = keepLastSeen;

        // The reflection loop above overwrites EVERY writable property with
        // no filter — an imported file must never smuggle a value past an
        // administrator mandate.
        Policy.PolicyService.ApplyMandatory(target);

        AppLogger.Info("Settings imported (trust tokens, secrets, gate state and policy-managed values preserved)");
    }
}
