using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Wrapp.Services;

namespace Wrapp.Models;

public class AppSettings
{
    /// <summary>
    /// One-shot flag -- true once OrgDefaultsSeeder has run for
    /// this profile (whether or not a defaults.local.json was present). Keeps
    /// org seeding from re-applying after a technician deliberately reverts a
    /// value back to its factory default.
    /// </summary>
    public bool OrgDefaultsSeeded { get; set; }

    // Subdirectory naming format appended when saving a new bundle.
    // Tokens: {Company} {Name} {Version} {DotVersion} {Language}
    public string DirectoryFormat { get; set; } = @"{Company}\{Name}\{Version}";

    // Folder name inside the bundle for the icon PNG.
    public string IconFolderName { get; set; } = "Icon";

    // Path to the extracted PSADT v4 template folder (contains Invoke-AppDeployToolkit.ps1/.exe, PSAppDeployToolkit/, etc.)
    public string PsadtTemplatePath { get; set; } = string.Empty;

    // UI color theme: "Dark" or "Light".
    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// Diagnostics: when true, UI interactions (navigation, clicks, dialogs,
    /// view builds) write [TRACE] lines to app.log - see <c>UiTrace</c>.
    /// Off by default; intended for reproducing freeze/stall reports.
    /// </summary>
    public bool VerboseUiTrace { get; set; }

    // Persisted Intune tenants (UI-wide, not per-bundle).
    public List<SavedTenantEntry> IntuneTenants { get; set; } = new();

    // Persisted SCCM sites (UI-wide, not per-bundle).
    public List<SavedSiteEntry> SccmSites { get; set; } = new();

    // Persisted domain defaults (UI-wide). Copied into Config.json on new
    // bundle creation; existing bundles keep whatever they already have.
    public List<SavedDomainEntry> Domains { get; set; } = new();

    // Custom {{Name}} placeholders. Sensitive rows keep Value
    // EMPTY here - their plaintext lives DPAPI-encrypted in the
    // placeholders.secure.json sidecar (PlaceholderSecureStore), so this file
    // and its exports stay portable while values stay machine-bound.
    public List<PlaceholderEntry> Placeholders { get; set; } = new();

    // Cached tenant display names resolved from Graph API during previous logins.
    // Key = tenant ID (GUID), Value = organization display name.
    // Mirrors IntuneManagement's Get-Setting {TenantId}_Name pattern.
    public Dictionary<string, string> TenantNameCache { get; set; } = new();

    // Azure DevOps repo URL for storing encryption keys.
    // Default value can be edited in this file. Users can override in Settings > Key Vault.
    public string KeyVaultRepoUrl { get; set; } = DefaultKeyVaultRepoUrl;

    // Edit this constant to set the default Key Vault repo URL for new installs.
    internal const string DefaultKeyVaultRepoUrl = "";

    /// <summary>
    /// When false, every Azure DevOps key vault
    /// read/write short-circuits via <see cref="Services.IFeatureGate"/> --
    /// UI controls stay visible but become no-ops, decrypt cascades skip the
    /// vault step, and packaging-time key captures log + discard instead of
    /// pushing. Defaults to true so existing installs keep their current
    /// behaviour; users opt out via Settings > Key Vault.
    /// </summary>
    public bool EnableAzureDevOpsKeyVault { get; set; } = true;

    /// <summary>
    /// Path template for auto-captured vault keys. Tokens:
    /// <c>{Tenant}</c>, <c>{AppId}</c>, <c>{AppName}</c>, <c>{PackageName}</c>,
    /// <c>{Date}</c>, <c>{Author}</c>. Default preserves the legacy layout
    /// so existing vaults still resolve via the legacy fast-path lookup; new
    /// or edited keys land at the templated path.
    /// </summary>
    public string KeyVaultPathTemplate { get; set; } = "/wrapp/{Tenant}/{AppId}.json";

    /// <summary>
    /// Path template for manually-saved vault keys (no
    /// <c>{Tenant}</c> / <c>{AppId}</c> available). Same token catalogue as
    /// <see cref="KeyVaultPathTemplate"/>; <c>{Tenant}</c> / <c>{AppId}</c>
    /// expand to empty strings.
    /// </summary>
    public string KeyVaultManualPathTemplate { get; set; } = "/manual/{PackageName}.json";

    /// <summary>
    /// When true, vault pushes create a feature branch + Pull
    /// Request instead of committing directly to <c>refs/heads/main</c>.
    /// For organisations that disallow direct main commits in their key
    /// vault repo. Defaults to false so existing installs keep their
    /// current commit behaviour.
    /// </summary>
    public bool KeyVaultUsePullRequests { get; set; } = false;

    /// <summary>
    /// Source branch name template for PR mode. Same tokens as
    /// <see cref="KeyVaultPathTemplate"/>. Default groups branches by date
    /// so a daily packaging run produces a single tidy folder of PRs.
    /// </summary>
    public string KeyVaultPrSourceBranchTemplate { get; set; } = "wrapp-keys/{Date}/{AppId}";

    /// <summary>
    /// PR title template. Same tokens as
    /// <see cref="KeyVaultPathTemplate"/>.
    /// </summary>
    public string KeyVaultPrTitleTemplate { get; set; } = "Wrapp: encryption keys for {AppName} ({AppId})";

    /// <summary>
    /// PR description template. Same tokens as
    /// <see cref="KeyVaultPathTemplate"/>. Multi-line; <c>\n</c> in the
    /// stored value becomes a real newline in the rendered PR body.
    /// </summary>
    public string KeyVaultPrDescriptionTemplate { get; set; }
        = "Automated push by Wrapp on {Date} by {Author}.\n\nApp: {AppName}\nTenant: {Tenant}\nApp ID: {AppId}";

    /// <summary>
    /// Machine-bound TOFU trust token for the approved
    /// <see cref="KeyVaultRepoUrl"/> - a DPAPI envelope (CurrentUser + per-app
    /// entropy) of the normalised URL, issued by
    /// <see cref="Services.EncryptionKeyStoreService.IssueKeyVaultTrustToken"/>
    /// when the user approves the URL in Settings. Empty until approved. Reads
    /// and pushes both verify it via
    /// <see cref="Services.EncryptionKeyStoreService.IsKeyVaultUrlTrusted"/>.
    /// <para>
    /// Property name retained for settings.json back-compat; the value is no
    /// longer a plain SHA-256 hash. The previous hash was forgeable - an
    /// attacker who could write settings.json could compute a matching hash for
    /// a malicious URL. A DPAPI token cannot be produced without executing as
    /// the current user, so this now genuinely prevents a settings.json edit or
    /// malicious config sync from redirecting key pushes. Legacy hash values
    /// simply fail verification and prompt a one-time re-approval.
    /// </para>
    /// </summary>
    public string KeyVaultRepoUrlHash { get; set; } = string.Empty;

    // =======================================================================
    // Gate framework: required user actions surfaced after a code /
    // config / version change (re-approval, consent, migrations). See
    // Services.Gates. Deliberately separate from the "dirty" (unsaved-edits)
    // concept -- these persist their resolution and can be version-triggered.
    // =======================================================================

    /// <summary>
    /// App version at the previous run. Set by <c>GateService.RecordRun</c> on
    /// each launch; version-triggered gates compare against it to detect an
    /// update (e.g. re-run a migration, re-prompt a bumped waiver).
    /// </summary>
    public string LastRunVersion { get; set; } = string.Empty;

    /// <summary>
    /// Per-gate resolution state keyed by <c>IAppGate.Id</c>. Free-form string
    /// values so a new gate can persist whatever it needs (a version, a bool, a
    /// timestamp) without an AppSettings schema change.
    /// </summary>
    public Dictionary<string, string> GateState { get; set; } = new();

    // =======================================================================
    // Installed-app update channel (Velopack).
    // =======================================================================

    /// <summary>
    /// Where Wrapp checks for updates: an HTTPS URL or a UNC share hosting a
    /// Velopack release feed (releases.win.json + packages). Empty = update
    /// checks disabled. Org-seedable via defaults.local.json; per-user trust
    /// approval is still required (see <see cref="UpdateFeedTrustToken"/>).
    /// </summary>
    public string UpdateFeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// "Auto" (updates are REQUIRED: at launch the user must update-and-restart
    /// or close Wrapp - the blocking-gate UX), "NotifyOnly" (check and
    /// announce; user chooses), or "Disabled". Orgs deploying the per-machine
    /// MSI (updates pushed via Intune/SCCM supersedence) should seed
    /// NotifyOnly or Disabled.
    /// </summary>
    public string UpdateMode { get; set; } = AppUpdateModes.Auto;

    /// <summary>
    /// Machine-bound TOFU trust token for the approved
    /// <see cref="UpdateFeedUrl"/> - the same DPAPI-envelope pattern as
    /// <see cref="KeyVaultRepoUrlHash"/>, issued by
    /// <see cref="Services.UpdateService.IssueFeedTrustToken"/> on user
    /// approval. The feed effectively delivers executable code, so an
    /// unapproved URL (e.g. written into settings.json or defaults.local.json
    /// by another process) must never be contacted silently.
    /// </summary>
    public string UpdateFeedTrustToken { get; set; } = string.Empty;

    /// <summary>
    /// Last app version whose What's-New notes the user has seen (set when the
    /// What's-New window is dismissed - deliberately NOT
    /// <see cref="LastRunVersion"/>, which GateService overwrites every
    /// launch). Empty on fresh profiles: the first launch after this feature
    /// ships shows the current version's notes once.
    /// </summary>
    public string LastSeenChangelogVersion { get; set; } = string.Empty;

    // =======================================================================
    // User-curated package defaults (Preferences layer).
    //
    // Each section mirrors the equivalent hashtable in Wrapp.Packager's
    // Defaults.psd1 so GUI-created packages match what the PS module would
    // emit from a bare config. See ModuleDefaultsSeed for the initial values.
    //
    // Metadata strings support template tokens ({{Company}}, {{Name}},
    // {{Date}}, {{Author}}, etc.) expanded via TemplateService.ApplyTokens
    // at AddPackage time.
    // =======================================================================

    public IntunePackageDefaults    IntunePackageDefaults    { get; set; } = new();
    public IntuneMetadataDefaults   IntuneMetadataDefaults   { get; set; } = new();
    public IntuneAssignmentDefaults IntuneAssignmentDefaults { get; set; } = new();
    public SccmPackageDefaults      SccmPackageDefaults      { get; set; } = new();
    public SccmMetadataDefaults     SccmMetadataDefaults     { get; set; } = new();
    public SccmDeploymentDefaults   SccmDeploymentDefaults   { get; set; } = new();

    // =======================================================================
    // Endpoint script defaults: paths the deploy/detect scripts use ON TARGET
    // MACHINES. Expanded into bundle scripts via {{TagFolder}} /
    // {{LocalAppFolder}} tokens when a bundle's Script/ folder is written;
    // exported to user-defaults.json so the PowerShell module sees the same
    // values (CLI parity). Existing bundles keep their baked paths.
    // =======================================================================

    /// <summary>Where deployed packages write detection results + transcript logs on the endpoint.</summary>
    public string EndpointTagFolder { get; set; } = ModuleDefaultsSeed.EndpointTagFolder;

    /// <summary>Root folder for per-app local assets (icons etc.) on the endpoint.</summary>
    public string EndpointLocalAppFolder { get; set; } = ModuleDefaultsSeed.EndpointLocalAppFolder;
}

/// <summary>
/// Mirrors <c>DefaultIntunePackageProperties</c> from Defaults.psd1 plus a few
/// additional Win32-app fields (CompanyPortalFeaturedApp, AzCopyWindowStyle,
/// UpdateMode) that are exposed by the Intune package editor. Seed values
/// live in <see cref="Wrapp.Services.ModuleDefaultsSeed"/>.
/// </summary>
public class IntunePackageDefaults
{
    // Initialized from ModuleDefaultsSeed so `new AppSettings()` is a VALID
    // configuration, not a blank one. AddPackage reads this POCO directly
    // (IntuneViewModel), while the Preferences editor seeds itself separately -
    // so leaving these at type defaults produced a package with
    // MaximumInstallationTimeInMinutes = 0 (invalid: must be 1-1440) on every
    // profile that had never saved Settings, while the Settings screen showed 60.
    public string Architecture                     { get; set; } = ModuleDefaultsSeed.IntuneArchitecture;
    public string MinimumSupportedWindowsRelease   { get; set; } = ModuleDefaultsSeed.IntuneMinimumSupportedWindowsRelease;
    public string InstallExperience                { get; set; } = ModuleDefaultsSeed.IntuneInstallExperience;
    public string RestartBehavior                  { get; set; } = ModuleDefaultsSeed.IntuneRestartBehavior;
    public int    MaximumInstallationTimeInMinutes { get; set; } = ModuleDefaultsSeed.IntuneMaximumInstallationTimeInMinutes;
    public bool   AllowAvailableUninstall          { get; set; } = ModuleDefaultsSeed.IntuneAllowAvailableUninstall;
    public bool   CompanyPortalFeaturedApp         { get; set; } = ModuleDefaultsSeed.IntuneCompanyPortalFeaturedApp;
    public bool   UseAzCopy                        { get; set; } = ModuleDefaultsSeed.IntuneUseAzCopy;
    public string AzCopyWindowStyle                { get; set; } = ModuleDefaultsSeed.IntuneAzCopyWindowStyle;
    public UpdateMode UpdateMode                   { get; set; } = ModuleDefaultsSeed.IntuneUpdateMode;
}

/// <summary>
/// Intune app metadata defaults - string templates expanded at AddPackage time.
/// Supports <c>{{Company}}, {{Name}}, {{Version}}, {{DotVersion}}, {{Language}},
/// {{Date}}, {{Author}}, {{EXEFile}}, {{MSIFile}}, {{GUID}}</c>.
/// </summary>
public class IntuneMetadataDefaults
{
    /// <summary>
    /// Token template used to default the AppName on newly-created Intune
    /// packages. Empty = fall back to the current App.Name (legacy behaviour).
    /// </summary>
    public string AppNameTemplate    { get; set; } = string.Empty;
    public string DeveloperTemplate  { get; set; } = string.Empty;
    public string OwnerTemplate      { get; set; } = string.Empty;
    public string CommentTemplate    { get; set; } = string.Empty;
    public string InformationURL     { get; set; } = string.Empty;
    public string PrivacyURL         { get; set; } = string.Empty;

    /// <summary>
    /// Token template used to default the operator note on new Intune
    /// packages. The packager merges it into the app's Notes JSON alongside
    /// the machine keys (CreatedBy/Guid/Date) it depends on - free text never
    /// replaces the tracking blob.
    /// </summary>
    public string NotesTemplate      { get; set; } = string.Empty;
}

/// <summary>
/// Mirrors <c>DefaultAssignmentProperties</c> plus the other assignment-fill
/// defaults (Intent, Type, GroupMode, etc.) the PS module applies.
/// </summary>
public class IntuneAssignmentDefaults
{
    // Seeded like the package blocks (see IntunePackageDefaults). DeadlineTime
    // and FilterMode stay empty - "no deadline" / "no filter" are the real
    // defaults, not unset values.
    public string Intent                         { get; set; } = ModuleDefaultsSeed.IntuneAssignmentIntent;
    public string Type                           { get; set; } = ModuleDefaultsSeed.IntuneAssignmentType;
    public string GroupMode                      { get; set; } = ModuleDefaultsSeed.IntuneAssignmentGroupMode;
    public string Notification                   { get; set; } = ModuleDefaultsSeed.IntuneAssignmentNotification;
    public string DeliveryOptimizationPriority   { get; set; } = ModuleDefaultsSeed.IntuneAssignmentDeliveryOptimizationPriority;
    public string AvailableTime                  { get; set; } = ModuleDefaultsSeed.IntuneAssignmentAvailableTime;
    public string DeadlineTime                   { get; set; } = ModuleDefaultsSeed.IntuneAssignmentDeadlineTime;
    public string FilterMode                     { get; set; } = ModuleDefaultsSeed.IntuneAssignmentFilterMode;
    /// <summary>
    /// When true, the restart grace period defaults below are emitted into
    /// per-assignment defaults; when false, Set-Win32AppAssignment strips them.
    /// </summary>
    public bool   EnableRestartGracePeriod       { get; set; }
    public int    RestartGracePeriodInMinutes    { get; set; }
    public int    RestartCountDownDisplayInMinutes { get; set; }
    public int    RestartNotificationSnoozeInMinutes { get; set; }
}

/// <summary>
/// Mirrors <c>DefaultSCCMPackageProperties</c> from Defaults.psd1.
/// </summary>
public class SccmPackageDefaults
{
    // Same reasoning as IntunePackageDefaults. The SCCM variant was SILENT:
    // SCCMViewModel copies these onto new packages and the SCCM validator
    // doesn't check runtimes, so zeroed Estimated/MaximumAllowedRuntimeMins
    // reached ConfigMgr with no badge and no warning.
    public string InstallationBehaviorType     { get; set; } = ModuleDefaultsSeed.SccmInstallationBehaviorType;
    public string LogonRequirementType         { get; set; } = ModuleDefaultsSeed.SccmLogonRequirementType;
    public string UserInteractionMode          { get; set; } = ModuleDefaultsSeed.SccmUserInteractionMode;
    public string RebootBehavior               { get; set; } = ModuleDefaultsSeed.SccmRebootBehavior;
    public string SlowNetworkDeploymentMode    { get; set; } = ModuleDefaultsSeed.SccmSlowNetworkDeploymentMode;
    public int    EstimatedRuntimeMins         { get; set; } = ModuleDefaultsSeed.SccmEstimatedRuntimeMins;
    public int    MaximumAllowedRuntimeMins    { get; set; } = ModuleDefaultsSeed.SccmMaximumAllowedRuntimeMins;
    public bool   InstallBehavior              { get; set; } = ModuleDefaultsSeed.SccmInstallBehavior;
    public bool   ContentFallback              { get; set; } = ModuleDefaultsSeed.SccmContentFallback;
}

/// <summary>
/// SCCM app metadata defaults - string templates expanded at AddPackage time.
/// Same token set as <see cref="IntuneMetadataDefaults"/>.
/// </summary>
public class SccmMetadataDefaults
{
    public string PublisherTemplate            { get; set; } = string.Empty;
    public string OwnerTemplate                { get; set; } = string.Empty;
    public string SupportContactTemplate       { get; set; } = string.Empty;
    public string DescriptionTemplate          { get; set; } = string.Empty;
    public string CommentTemplate              { get; set; } = string.Empty;
    public string NameTemplate                 { get; set; } = string.Empty;
    public string LocalizedNameTemplate        { get; set; } = string.Empty;
    public string LocalizedDescriptionTemplate { get; set; } = string.Empty;
    public string KeywordsTemplate             { get; set; } = string.Empty;
    public string PrivacyUrl                   { get; set; } = string.Empty;
    public string UserDocumentation            { get; set; } = string.Empty;
    public string LinkText                     { get; set; } = string.Empty;
    public string ReleaseDateTemplate          { get; set; } = string.Empty;
    public bool   IsFeatured                   { get; set; }
    public bool   AutoInstall                  { get; set; }
}

/// <summary>
/// Mirrors <c>DefaultDeploymentProperties</c> from Defaults.psd1.
/// </summary>
public class SccmDeploymentDefaults
{
    public string DeployAction      { get; set; } = ModuleDefaultsSeed.SccmDeployAction;
    public string DeployPurpose     { get; set; } = ModuleDefaultsSeed.SccmDeployPurpose;
    public string UserNotification  { get; set; } = ModuleDefaultsSeed.SccmUserNotification;
    public string TimeBaseOn        { get; set; } = ModuleDefaultsSeed.SccmTimeBaseOn;
    public string AvailableDateTime { get; set; } = ModuleDefaultsSeed.SccmAvailableDateTime;
    // Empty = no deadline, a real default rather than an unset value.
    public string DeadlineDateTime  { get; set; } = ModuleDefaultsSeed.SccmDeadlineDateTime;
}

/// <summary>Simple DTO for persisting Intune tenant settings to settings.json.</summary>
public class SavedTenantEntry
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Legacy field from pre-v3.2 settings where Domain held the tenant ID GUID
    /// and Key held the display name. Read during deserialization for migration only;
    /// excluded from new saves via JsonIgnore(WriteIndeterminate).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Domain { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string ClientID { get; set; } = string.Empty;
    public AuthFlow AuthFlow { get; set; } = AuthFlow.Interactive;
    public string ClientSecret { get; set; } = string.Empty;
    public string CertThumbprint { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string MinimumSupportedWindowsRelease { get; set; } = string.Empty;
    public string IntuneWinPath { get; set; } = string.Empty;
    public string IconFolder { get; set; } = string.Empty;
}

/// <summary>Simple DTO for persisting SCCM site settings to settings.json.</summary>
public class SavedSiteEntry
{
    public string Key { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string AppFolder { get; set; } = string.Empty;
    public string IconFolder { get; set; } = string.Empty;
    public List<string> DeploymentGroups { get; set; } = new();
}

/// <summary>
/// Simple DTO for persisting domain defaults to settings.json. Mirrors
/// <see cref="Wrapp.Models.DomainEntry"/> field-for-field so new bundles can
/// deep-copy the list into their own <c>Config.Domain</c> section.
/// </summary>
public class SavedDomainEntry
{
    public string Key        { get; set; } = string.Empty;
    public string IsDistPath { get; set; } = string.Empty;
    public string AppFolder  { get; set; } = string.Empty;
    public string TagFolder  { get; set; } = string.Empty;
}

/// <summary>
/// One custom <c>{{Name}}</c> placeholder. When
/// <see cref="IsSensitive"/> is true, <see cref="Value"/> stays EMPTY in
/// settings.json - the plaintext lives in the DPAPI sidecar
/// (PlaceholderSecureStore), keyed by <see cref="Name"/>.
/// </summary>
public class PlaceholderEntry
{
    public string Name        { get; set; } = string.Empty;
    public string Value       { get; set; } = string.Empty;
    public bool   IsSensitive { get; set; }
    public string Comment     { get; set; } = string.Empty;
}

/// <summary>
/// Thrown when DPAPI encryption fails. Callers MUST refuse to persist the
/// secret rather than fall back to plaintext.
/// </summary>
public sealed class SecretEncryptionException : Exception
{
    public SecretEncryptionException(string message, Exception? inner = null)
        : base(message, inner) { }
}

public static class SecretProtection
{
    // ── Envelope constants ────────────────────────────────────────────

    // String (settings.json) envelopes:
    //   v1 legacy: "dpapi:<base64>"        -- ProtectedData.Protect(plain, null)
    //   v2 current: "dpapi:v2:<base64>"     -- ProtectedData.Protect(plain, _entropy)
    //
    // Byte (MSAL cache, key store) envelopes:
    //   v1 legacy: raw ProtectedData.Protect(plain, null) bytes
    //   v2 current: [0x57 0x52 0x02] + ProtectedData.Protect(plain, _entropy)
    //
    // Readers detect format by prefix / magic and fall back to v1 when absent,
    // so existing on-disk ciphertext from prior app versions stays decryptable.
    // Writers always emit v2. Callers that read v1 should re-write to upgrade
    // on the next save (self-healing migration).
    private const string Prefix   = "dpapi:";
    private const string V2Prefix = "dpapi:v2:";
    private static readonly byte[] _v2ByteMagic = { 0x57, 0x52, 0x02 }; // "WR" + v2

    // 16-byte entropy added to every v2 DPAPI call. Defence-in-depth against
    // same-user code that doesn't ship with Wrapp's source: a malicious
    // sibling process running as the current user must supply this exact byte
    // sequence to ProtectedData.Unprotect() to recover the plaintext. This is
    // NOT a secret against a targeted attacker who has the Wrapp binary --
    // they can read these bytes out of the DLL. It raises the cost of
    // opportunistic DPAPI-blob harvesting without overselling itself.
    //
    // Must NEVER change: any change would strand every v2-format ciphertext
    // already written to disk (they would decrypt with the old entropy only,
    // but the v2 path would supply the new entropy and fail).
    private static readonly byte[] _entropy =
    {
        0x57, 0x72, 0x61, 0x70, 0x70, 0x2E, 0x47, 0x55,
        0x49, 0x2E, 0x44, 0x50, 0x41, 0x50, 0x49, 0x2D
    };

    /// <summary>
    /// Encrypts a plaintext secret using DPAPI with per-app entropy. Returns
    /// "dpapi:v2:BASE64" string (empty string if input is null/empty).
    /// Throws <see cref="SecretEncryptionException"/> when DPAPI is unavailable
    /// (broken user profile, running as a service, roamed credentials). Callers
    /// must surface this to the user and refuse to persist rather than downgrade
    /// to plaintext.
    /// </summary>
    public static string Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted  = ProtectedData.Protect(plainBytes, _entropy, DataProtectionScope.CurrentUser);
            return V2Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            throw new SecretEncryptionException(
                "DPAPI encryption failed. This typically means the user profile " +
                "cannot access its DPAPI master key (roamed profile, service " +
                "account, or OS reinstall). The secret will not be saved.", ex);
        }
    }

    /// <summary>
    /// Decrypts a DPAPI-protected secret. Recognises three formats:
    /// <list type="bullet">
    /// <item>"dpapi:v2:BASE64" -- current, entropy-protected.</item>
    /// <item>"dpapi:BASE64"    -- legacy, no entropy. Decrypts for backward
    /// compatibility; callers should re-save to upgrade to v2.</item>
    /// <item>Anything else     -- legacy plaintext, returned as-is.</item>
    /// </list>
    /// Returns empty string on decrypt failure (different user, corrupted,
    /// missing DPAPI key) so callers don't crash on stale caches.
    /// </summary>
    public static string Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;

        // Check v2 BEFORE v1 -- V2Prefix starts with Prefix, so the longer
        // prefix match has to come first.
        if (stored.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            try
            {
                var base64     = stored.Substring(V2Prefix.Length);
                var encrypted  = Convert.FromBase64String(base64);
                var plainBytes = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch { return string.Empty; }
        }

        if (stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                var base64     = stored.Substring(Prefix.Length);
                var encrypted  = Convert.FromBase64String(base64);
                var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch { return string.Empty; }
        }

        return stored; // legacy plaintext
    }

    /// <summary>
    /// Strict variant of <see cref="Decrypt"/>
    /// for integrity-sensitive values (e.g. the Key Vault trust token). Decrypts
    /// ONLY genuine DPAPI envelopes ("dpapi:v2:" or legacy "dpapi:"); returns
    /// <c>null</c> for everything else. Crucially it does NOT pass through a
    /// non-prefixed "legacy plaintext" value, so a string an attacker wrote
    /// directly into settings.json cannot be accepted as authentic - only a
    /// value that was DPAPI-protected as the current user round-trips.
    /// </summary>
    public static string? DecryptAuthentic(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        if (stored.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            try
            {
                var encrypted  = Convert.FromBase64String(stored.Substring(V2Prefix.Length));
                var plainBytes = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch { return null; }
        }

        if (stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            try
            {
                var encrypted  = Convert.FromBase64String(stored.Substring(Prefix.Length));
                var plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch { return null; }
        }

        return null; // NOT a DPAPI envelope -- refuse (no plaintext passthrough)
    }

    // ── Byte-array helpers for MSAL cache + EncryptionKeyStore payloads ──

    /// <summary>
    /// DPAPI-protects a raw byte array with the shared per-app entropy and
    /// prepends the v2 magic (<c>0x57 0x52 0x02</c>, "WR" + version). Use for
    /// binary caches that aren't Base64-wrapped in a string envelope
    /// (MSAL token cache, encryption-key-store payloads).
    /// </summary>
    public static byte[] ProtectBytes(byte[] plain)
    {
        if (plain is null) throw new ArgumentNullException(nameof(plain));
        var encrypted = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);
        var result    = new byte[_v2ByteMagic.Length + encrypted.Length];
        Buffer.BlockCopy(_v2ByteMagic, 0, result, 0, _v2ByteMagic.Length);
        Buffer.BlockCopy(encrypted, 0, result, _v2ByteMagic.Length, encrypted.Length);
        return result;
    }

    /// <summary>
    /// Undoes <see cref="ProtectBytes"/>. Recognises v2 format (magic prefix +
    /// entropy-protected payload) and falls back to v1 (raw ProtectedData.Protect
    /// without entropy) so on-disk caches written by older app versions still
    /// decrypt. Callers should write with <see cref="ProtectBytes"/> after read
    /// to self-heal v1 → v2 over time.
    /// </summary>
    public static byte[] UnprotectBytes(byte[] stored)
    {
        if (stored is null) throw new ArgumentNullException(nameof(stored));
        if (HasV2ByteMagic(stored))
        {
            var payload = new byte[stored.Length - _v2ByteMagic.Length];
            Buffer.BlockCopy(stored, _v2ByteMagic.Length, payload, 0, payload.Length);
            return ProtectedData.Unprotect(payload, _entropy, DataProtectionScope.CurrentUser);
        }
        // v1: legacy, no entropy.
        return ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser);
    }

    private static bool HasV2ByteMagic(byte[] data)
    {
        if (data.Length < _v2ByteMagic.Length) return false;
        for (int i = 0; i < _v2ByteMagic.Length; i++)
            if (data[i] != _v2ByteMagic[i]) return false;
        return true;
    }

    // -----------------------------------------------------------------------
    // SecureString helpers - narrow the plaintext lifetime window.
    // -----------------------------------------------------------------------
    //
    // Wrapp is Windows-only (WPF + DPAPI). SecureString's memory-encryption
    // guarantee only applies on Windows; .NET's newer "avoid SecureString"
    // guidance is aimed at cross-platform consumers. For this app, routing
    // secrets through SecureString means the plaintext string only exists
    // inside a Marshal.SecureStringToBSTR → PtrToStringAuto → ZeroFreeBSTR
    // scope, not as a long-lived CLR-interned string.

    /// <summary>
    /// Decrypts a DPAPI-protected secret directly into a <see cref="SecureString"/>,
    /// appending each character without ever materializing a plaintext
    /// <see cref="string"/>. Returns <c>null</c> when input is null/empty; returns
    /// a legacy plaintext value wrapped in a SecureString when no "dpapi:" prefix
    /// is present (backward-compat with settings written before the DPAPI path).
    /// </summary>
    public static SecureString? DecryptToSecureString(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        byte[]? plainBytes = null;
        try
        {
            // v2 (current): entropy-protected. Check BEFORE v1 since V2Prefix
            // starts with Prefix.
            if (stored.StartsWith(V2Prefix, StringComparison.Ordinal))
            {
                var base64 = stored.Substring(V2Prefix.Length);
                var encrypted = Convert.FromBase64String(base64);
                plainBytes = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            }
            else if (stored.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // v1 legacy: no entropy.
                var base64 = stored.Substring(Prefix.Length);
                var encrypted = Convert.FromBase64String(base64);
                plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            }
            else
            {
                // Legacy plaintext - no way to avoid the string copy here; best
                // effort is to build a SecureString and let the caller drop the
                // original. Callers should re-save ASAP to move to DPAPI form.
                // A non-DPAPI secret in settings.json is
                // either a pre-DPAPI legacy value OR an attacker-injected
                // substitution (settings.json is user-writable). Wrapp always
                // writes v2 envelopes, so log this as suspicious for visibility.
                // NOTE: hard rejection is deferred until a migration flag exists
                // that can distinguish legacy values from tampering without
                // stranding old installs.
                AppLogger.Warn("SecretProtection: loaded a NON-DPAPI (plaintext) secret value. " +
                    "If you did not import a legacy config, verify settings.json was not edited by another tool. " +
                    "Re-save in Settings to upgrade it to an encrypted envelope.");
                return FromPlaintextChars(stored.AsSpan());
            }

            var chars = Encoding.UTF8.GetChars(plainBytes);
            try
            {
                return FromPlaintextChars(chars);
            }
            finally
            {
                Array.Clear(chars, 0, chars.Length);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (plainBytes is not null)
                CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// Wraps a freshly-typed plaintext value (e.g. from a WPF
    /// <see cref="System.Windows.Controls.PasswordBox"/>'s Password property)
    /// into a SecureString. Prefer <see cref="System.Windows.Controls.PasswordBox.SecurePassword"/>
    /// when the caller has it; this overload exists for legacy string entry points.
    /// </summary>
    public static SecureString ToSecureString(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return new SecureString();
        return FromPlaintextChars(plaintext.AsSpan());
    }

    private static SecureString FromPlaintextChars(ReadOnlySpan<char> chars)
    {
        var s = new SecureString();
        foreach (var c in chars) s.AppendChar(c);
        s.MakeReadOnly();
        return s;
    }

    /// <summary>
    /// Resolves a tenant's ClientSecret into a fresh
    /// <see cref="SecureString"/> for consumption at the MSAL/PS boundary,
    /// for callers whose fresh value is already a <see cref="SecureString"/>
    /// (the common case after PasswordBox.SecurePassword writes directly to
    /// <see cref="IntuneTenantEntry.ClientSecret"/>). When <paramref name="freshSecret"/>
    /// has non-zero length it is copied into a fresh, read-only SecureString
    /// so the caller can dispose its returned value without affecting the
    /// model entry. When the fresh value is null or empty, falls back to
    /// <see cref="DecryptToSecureString"/> on <paramref name="cipher"/>.
    /// </summary>
    public static SecureString? ResolveTenantSecret(string? cipher, SecureString? freshSecret)
    {
        if (freshSecret is { Length: > 0 })
        {
            return WithPlaintext(freshSecret, p => ToSecureString(p));
        }
        return DecryptToSecureString(cipher);
    }

    /// <summary>
    /// Unwraps <paramref name="secret"/> to plaintext for the duration of
    /// <paramref name="body"/>, then zeroes the unmanaged buffer via
    /// <see cref="Marshal.ZeroFreeBSTR"/>. The plaintext .NET <see cref="string"/>
    /// passed to <paramref name="body"/> is a managed copy that the GC owns -
    /// callers should keep <paramref name="body"/> short and avoid retaining
    /// the plaintext parameter. Throws when <paramref name="secret"/> is null.
    /// </summary>
    public static T WithPlaintext<T>(SecureString secret, Func<string, T> body)
    {
        if (secret is null) throw new ArgumentNullException(nameof(secret));

        IntPtr bstr = IntPtr.Zero;
        try
        {
            bstr = Marshal.SecureStringToBSTR(secret);
            var plain = Marshal.PtrToStringBSTR(bstr) ?? string.Empty;
            try
            {
                return body(plain);
            }
            catch (Exception ex)
            {
                // If the callee throws while holding the plaintext, the
                // exception's Message / StackTrace / InnerException may
                // quote the secret value back -- e.g. "Invalid client
                // secret 'abc123'". AppLogger's Redact pass recognises
                // well-known token shapes (Bearer, JWT, ClientSecret=)
                // but a bare opaque secret in a free-form exception
                // message would slip through. Sanitize the full trace for
                // the log, and throw a fresh exception without the inner
                // one (inner's .Message / .StackTrace may retain plaintext
                // that we cannot mutate in place).
                var safeMsg = string.IsNullOrEmpty(plain)
                    ? (ex.Message ?? "")
                    : (ex.Message ?? "").Replace(plain, "***");
                var safeTrace = string.IsNullOrEmpty(plain)
                    ? ex.ToString()
                    : ex.ToString().Replace(plain, "***");
                AppLogger.Error(
                    $"SecretProtection.WithPlaintext: body threw (sanitized): {safeTrace}");
                throw new InvalidOperationException(
                    $"Secret-consuming operation failed: {safeMsg}");
            }
        }
        finally
        {
            if (bstr != IntPtr.Zero) Marshal.ZeroFreeBSTR(bstr);
        }
    }
}
