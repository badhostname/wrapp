using System.IO;
using System.Text.Json;
using Wrapp.Models;

namespace Wrapp.Services;

public static class SettingsService
{
    // -----------------------------------------------------------------------
    // Preferences persistence
    // -----------------------------------------------------------------------

    /// <summary>
    /// Result of a <see cref="SavePreferencesAsync"/> call. <c>Saved == false</c>
    /// means the save was aborted — typically because DPAPI encryption failed on
    /// at least one tenant's ClientSecret. Callers should surface the
    /// <see cref="Error"/> to the user and leave the UI in its current state.
    /// </summary>
    public readonly record struct SavePreferencesResult(bool Saved, string? Error);

    /// <summary>
    /// Projects in-memory <see cref="IntuneTenantEntry"/> / <see cref="SCCMSiteEntry"/>
    /// collections into persisted <see cref="SavedTenantEntry"/> / <see cref="SavedSiteEntry"/>
    /// form, DPAPI-encrypts each tenant's ClientSecret, and writes settings.json
    /// atomically. Preserves other fields on <paramref name="settings"/> (theme,
    /// paths, TenantNameCache, etc.).
    ///
    /// <para>Returns <c>Saved=false</c> when DPAPI encryption fails. In that case
    /// <c>settings.json</c> is NOT modified — the user's prior state remains on
    /// disk. Caller should surface the error via FluentDialog.</para>
    /// </summary>
    public static async Task<SavePreferencesResult> SavePreferencesAsync(
        AppSettings settings,
        IEnumerable<IntuneTenantEntry> intuneTenants,
        IEnumerable<SCCMSiteEntry> sccmSites,
        IEnumerable<DomainEntry>? domains         = null,
        IntunePackageDefaults?    intunePackage   = null,
        IntuneMetadataDefaults?   intuneMetadata  = null,
        IntuneAssignmentDefaults? intuneAssignment = null,
        SccmPackageDefaults?      sccmPackage     = null,
        SccmMetadataDefaults?     sccmMetadata    = null,
        SccmDeploymentDefaults?   sccmDeployment  = null,
        string?                   endpointTagFolder      = null,
        string?                   endpointLocalAppFolder = null)
    {
        List<SavedTenantEntry> mappedTenants;
        List<SavedSiteEntry>   mappedSites;
        List<SavedDomainEntry>? mappedDomains = null;

        try
        {
            mappedTenants = intuneTenants.Select(t => new SavedTenantEntry
            {
                Key                            = t.Key,
                Name                           = t.Name,
                Comment                        = t.Comment,
                ClientID                       = t.ClientID,
                AuthFlow                       = t.AuthFlow,
                // If the user typed a fresh SecureString value, encrypt it.
                // Otherwise preserve the existing DPAPI ciphertext byte-for-byte
                // so round-trip loads don't churn the ciphertext (DPAPI is
                // non-deterministic — re-encrypting the same plaintext yields a
                // different base64 string each time, which would flag the
                // snapshot as "dirty" on every open).
                //
                // Phase 15 (S-6): the fresh value is a SecureString rather
                // than a managed string. WithPlaintext narrows the
                // plaintext-in-memory window to just the DPAPI encrypt call;
                // the BSTR backing it is zero-freed on scope exit.
                ClientSecret                   = t.ClientSecret is { Length: > 0 }
                                                     ? SecretProtection.WithPlaintext(t.ClientSecret, SecretProtection.Encrypt)
                                                     : t.ClientSecretCipher,
                CertThumbprint                 = t.CertThumbprint,
                Architecture                   = t.Architecture,
                MinimumSupportedWindowsRelease = t.MinimumSupportedWindowsRelease,
                IntuneWinPath                  = t.IntuneWinPath,
                IconFolder                     = t.IconFolder,
            }).ToList();

            mappedSites = sccmSites.Select(s =>
            {
                var entry = new SavedSiteEntry
                {
                    Key        = s.Key,
                    Comment    = s.Comment,
                    AppFolder  = s.AppFolder,
                    IconFolder = s.IconFolder,
                };
                foreach (var g in s.DeploymentGroups)
                    entry.DeploymentGroups.Add(g);
                return entry;
            }).ToList();

            if (domains is not null)
            {
                mappedDomains = domains.Select(d => new SavedDomainEntry
                {
                    Key        = d.Key,
                    IsDistPath = d.IsDistPath,
                    AppFolder  = d.AppFolder,
                    TagFolder  = d.TagFolder,
                }).ToList();
            }
        }
        catch (SecretEncryptionException ex)
        {
            AppLogger.Exception("Settings: SavePreferencesAsync failed", ex);
            return new SavePreferencesResult(false, ex.Message);
        }

        settings.IntuneTenants = mappedTenants;
        settings.SccmSites     = mappedSites;
        if (mappedDomains  is not null) settings.Domains                    = mappedDomains;

        // Scalar default sections — only overwrite when the caller supplied a
        // replacement. Preserves whatever's on disk for callers that haven't
        // opted into the new preferences (e.g. legacy tenant-only save path).
        if (intunePackage    is not null) settings.IntunePackageDefaults    = intunePackage;
        if (intuneMetadata   is not null) settings.IntuneMetadataDefaults   = intuneMetadata;
        if (intuneAssignment is not null) settings.IntuneAssignmentDefaults = intuneAssignment;
        if (sccmPackage      is not null) settings.SccmPackageDefaults      = sccmPackage;
        if (sccmMetadata     is not null) settings.SccmMetadataDefaults     = sccmMetadata;
        if (sccmDeployment   is not null) settings.SccmDeploymentDefaults   = sccmDeployment;
        if (endpointTagFolder      is not null) settings.EndpointTagFolder      = endpointTagFolder;
        if (endpointLocalAppFolder is not null) settings.EndpointLocalAppFolder = endpointLocalAppFolder;

        try
        {
            var json = JsonSerializer.Serialize(settings, WriteOptions);

            // M2: settings.json + the user-defaults sidecar are written under
            // ONE cross-process hold so another instance can never observe (or
            // interleave between) half of the pair.
            await CrossProcessLock.RunAsync("Settings", () =>
            {
                AtomicFile.WriteAllText(SettingsPath, json);
                UserDefaultsService.Export(settings);
            });
            AppLogger.Info($"Preferences saved: {mappedTenants.Count} tenant(s), {mappedSites.Count} site(s){(mappedDomains is not null ? $", {mappedDomains.Count} domain(s)" : "")}");

            // Sync the persisted ciphertext back to the in-memory entries, then
            // dispose and unroot the transient SecureString. Next load/diff/run
            // uses the cipher; no plaintext lingers on the model.
            var livetenants = intuneTenants.ToList();
            for (int i = 0; i < livetenants.Count && i < mappedTenants.Count; i++)
            {
                livetenants[i].ClientSecretCipher = mappedTenants[i].ClientSecret;
                livetenants[i].ClientSecret?.Dispose();
                livetenants[i].ClientSecret       = null;
            }

            return new SavePreferencesResult(true, null);
        }
        catch (Exception ex)
        {
            AppLogger.Exception("Settings: preferences write failed", ex);
            return new SavePreferencesResult(false, ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Load / save (scalars)
    // -----------------------------------------------------------------------

    // Resolved via PlatformConfig (env var > appsettings.json > default).
    private static readonly string SettingsPath = PlatformConfig.SettingsPath;

    private static readonly JsonSerializerOptions WriteOptions = JsonDefaults.Pretty;
    // ReadOptions carries the same enum converter as Write so enum-typed
    // properties (UpdateMode, AuthFlow, ...) round-trip through string
    // representations -- default JsonSerializer options would expect integer
    // enum values and fail to deserialise the canonical "Create" string.
    private static readonly JsonSerializerOptions ReadOptions = JsonDefaults.CaseInsensitive;

    /// <summary>
    /// True when <see cref="Load"/> found the primary settings.json present but
    /// corrupt (parse failure). Startup code can read this after Load to surface
    /// a one-time warning to the user that their settings may have been recovered
    /// from backup or reset to defaults. Reset by a successful <see cref="Save"/>.
    /// </summary>
    public static bool PrimaryWasCorruptOnLoad { get; private set; }

    public static AppSettings Load()
    {
        // M2: serialized against concurrent instance saves so the primary/.bak
        // recovery logic never runs against a mid-cycle pair.
        AppSettings result = new();
        CrossProcessLock.Run("Settings", () => result = LoadCore());
        return result;
    }

    private static AppSettings LoadCore()
    {
        var backup = SettingsPath + ".bak";

        // STA-1 (2026-07 audit): isolate the primary read+parse in its OWN try so
        // a CORRUPT (not merely missing) primary falls through to the .bak
        // recovery below. Previously both attempts shared one try, so a parse
        // failure on the primary jumped straight to "return defaults" and
        // silently discarded all saved tenants/sites/theme AND the usable backup.
        if (File.Exists(SettingsPath))
        {
            try
            {
                // SEC-8: a settings.json in the MB range is corrupt, not big.
                var size = new FileInfo(SettingsPath).Length;
                if (size > 5 * 1024 * 1024)
                    throw new InvalidOperationException($"settings.json is {size / 1024} KB — refusing as corrupt");

                var json = File.ReadAllText(SettingsPath);
                var result = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
                if (result is not null)
                {
                    // Repair structurally-invalid defaults left by profiles written
                    // before the defaults POCOs had initializers (see SettingsRepair).
                    SettingsRepair.Apply(result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                PrimaryWasCorruptOnLoad = true;
                AppLogger.Warn($"SettingsService.Load: primary settings.json is unreadable/corrupt " +
                               $"({ex.Message}); attempting backup recovery before falling back to defaults");
            }
        }

        // Recovery: torn-write on a prior save, OR a corrupt primary (above).
        if (File.Exists(backup))
        {
            try
            {
                AppLogger.Warn($"SettingsService.Load: falling back to backup {backup}");
                var json = File.ReadAllText(backup);
                var result = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
                if (result is not null)
                {
                    SettingsRepair.Apply(result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Settings: backup also unreadable; returning defaults (saved tenants/sites lost)", ex);
            }
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, WriteOptions);
            // M2: whole write cycle under the cross-process settings lock.
            CrossProcessLock.Run("Settings", () => AtomicFile.WriteAllText(SettingsPath, json));
            PrimaryWasCorruptOnLoad = false; // a clean write clears the recovery flag
        }
        catch (Exception ex)
        {
            AppLogger.Exception("Settings: save failed", ex);
        }
    }
}
