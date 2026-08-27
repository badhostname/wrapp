using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Manages encryption keys for Intune Win32 app content. Keys are stored
/// exclusively in the configured Azure DevOps Git repo (a "vault"). There
/// is no per-workstation local cache -- having encryption keys scattered
/// across every workstation Wrapp runs on is bad practice; the DevOps
/// repo's ACL is the access control surface.
/// </summary>
public class EncryptionKeyStoreService
{
    private readonly DevOpsAuthService _devOpsAuth;
    private readonly AppSettings _settings;
    private readonly IFeatureGate _featureGate;
    private static readonly HttpClient Http = new();
    // Encryption keys contain Base64; the relaxed encoder preserves + and /
    // as-is so keys round-trip correctly when read as raw text.
    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.PrettyUnsafe;

    // Azure DevOps resource ID for OAuth
    private const string DevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

    public EncryptionKeyStoreService(
        DevOpsAuthService devOpsAuth,
        AppSettings settings,
        IFeatureGate featureGate)
    {
        _devOpsAuth = devOpsAuth;
        _settings = settings;
        _featureGate = featureGate;
    }

    // -----------------------------------------------------------------------
    // Public API -- all DevOps-backed
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pushes encryption keys to the DevOps vault. Throws
    /// <see cref="DevOpsVaultNotConfiguredException"/> when the
    /// <see cref="WrappFeatures.AzureDevOpsKeyVault"/> gate is OFF (user
    /// opted out, or <see cref="AppSettings.KeyVaultRepoUrl"/> is empty).
    /// </summary>
    public async Task SaveKeysAsync(EncryptionKeyInfo keys)
    {
        if (!_featureGate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault))
            throw new DevOpsVaultNotConfiguredException(
                _featureGate.DescribeWhyDisabled(WrappFeatures.AzureDevOpsKeyVault));

        bool isManual = string.IsNullOrEmpty(keys.AppId) || string.IsNullOrEmpty(keys.TenantId);
        var label = isManual ? keys.PackageName : keys.AppId;

        // Phase 16d. Route to PR mode when the user has opted in. For
        // organisations that disallow direct main commits in their key
        // vault repo, every save becomes a feature branch + PR instead.
        if (_settings.KeyVaultUsePullRequests)
        {
            await PushAsPullRequestAsync(keys, isManual);
            AppLogger.Info($"KeyStore: opened PR for '{label}' on DevOps");
        }
        else
        {
            await PushToDevOpsAsync(keys, isManual);
            AppLogger.Info($"KeyStore: pushed keys for '{label}' to DevOps");
        }
    }

    /// <summary>
    /// Fetches encryption keys from the DevOps vault. Returns null when not
    /// found, when the gate is OFF, or when an HTTP error occurs. The
    /// silent-null contract lets cascade flows (decrypt orchestrator,
    /// inventory import) fall through to manual / brute-force paths
    /// without dialog churn.
    /// </summary>
    public async Task<EncryptionKeyInfo?> GetKeysAsync(string tenantId, string appId)
    {
        if (!_featureGate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault)) return null;

        try
        {
            return await FetchFromDevOpsAsync(tenantId, appId);
        }
        catch (Exception ex)
        {
            AppLogger.Exception($"KeyStore: DevOps fetch failed for {appId}", ex);
            return null;
        }
    }

    /// <summary>Checks if keys exist in the DevOps vault. Returns false silently when the gate is OFF.</summary>
    public async Task<bool> HasKeysAsync(string tenantId, string appId)
    {
        if (!_featureGate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault)) return false;
        try
        {
            return await CheckDevOpsExistsAsync(tenantId, appId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerates all encryption keys from the Azure DevOps vault repo.
    /// Uses the Git Items API with recursion to list all .json files,
    /// then fetches each one's content. Returns an empty list when the
    /// <see cref="WrappFeatures.AzureDevOpsKeyVault"/> gate is OFF; that
    /// causes <see cref="IntuneWinDecryptOrchestrator.BruteForceDecryptAsync"/>
    /// to surface a friendly "switch to manual / CSV" message instead of
    /// firing an HTTP enumeration that would just fail.
    /// </summary>
    public async Task<List<(EncryptionKeyInfo Key, string RelativePath)>> LoadAllDevOpsKeysAsync()
    {
        if (!_featureGate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault))
            return new List<(EncryptionKeyInfo, string)>();

        // SEC-1 (2026-07 audit): the TOFU trust check now guards READS too, not
        // just pushes. An unapproved/redirected URL must not silently pull
        // attacker-controlled key material into the decrypt cascade.
        if (!IsKeyVaultUrlTrusted(_settings.KeyVaultRepoUrl, _settings.KeyVaultRepoUrlHash))
        {
            AppLogger.Warn("KeyStore: vault URL not approved on this machine -- skipping enumeration. Approve it in Settings -> Key Vault.");
            return new List<(EncryptionKeyInfo, string)>();
        }

        var results = new List<(EncryptionKeyInfo, string)>();

        var parsed = ParseRepoUrl();
        if (parsed is null) return results;
        var (org, project, repo) = parsed.Value;

        var token = await GetDevOpsTokenAsync();
        if (token is null)
        {
            AppLogger.Warn("KeyStore: cannot enumerate DevOps vault -- no token");
            return results;
        }

        try
        {
            var apiBase = $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}";

            // List all items recursively
            var listUrl = $"{apiBase}/items?recursionLevel=Full&api-version=7.0";
            var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
            listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var listResp = await Http.SendAsync(listReq);
            if (!listResp.IsSuccessStatusCode)
            {
                AppLogger.Warn($"KeyStore: DevOps items list failed -- {listResp.StatusCode}");
                return results;
            }

            var listJson = await listResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(listJson);

            if (!doc.RootElement.TryGetProperty("value", out var items))
                return results;

            // Collect all .json file paths (skip folders)
            var jsonPaths = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                // Folders have isFolder=true; files either have isFolder=false or no isFolder property
                if (item.TryGetProperty("isFolder", out var isFolder) && isFolder.GetBoolean())
                    continue;
                if (!item.TryGetProperty("path", out var pathProp))
                    continue;
                var path = pathProp.GetString();
                if (path is not null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    jsonPaths.Add(path);
            }

            AppLogger.Info($"KeyStore: found {jsonPaths.Count} key file(s) in DevOps vault");

            // Fetch each file's raw content
            foreach (var filePath in jsonPaths)
            {
                try
                {
                    var fileUrl = $"{apiBase}/items?path={Uri.EscapeDataString(filePath)}&api-version=7.0";
                    var fileReq = new HttpRequestMessage(HttpMethod.Get, fileUrl);
                    fileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    // Accept octet-stream to get raw file content, not item metadata
                    fileReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

                    var fileResp = await Http.SendAsync(fileReq);
                    if (!fileResp.IsSuccessStatusCode) continue;

                    var json = await fileResp.Content.ReadAsStringAsync();
                    var key = JsonSerializer.Deserialize<EncryptionKeyInfo>(json);
                    if (key is not null && !string.IsNullOrEmpty(key.EncryptionKey))
                    {
                        var relative = filePath.TrimStart('/');
                        results.Add((key, relative));
                    }
                    else
                    {
                        AppLogger.Warn($"KeyStore: DevOps key at {filePath} has no EncryptionKey after deserialize");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Exception($"KeyStore: failed to fetch DevOps key at {filePath}", ex);
                }
            }

            AppLogger.Info($"KeyStore: loaded {results.Count} key(s) from DevOps vault");
        }
        catch (Exception ex)
        {
            AppLogger.Exception("KeyStore: DevOps vault enumeration failed", ex);
        }

        return results;
    }

    // -----------------------------------------------------------------------
    // Azure DevOps Git REST API
    // -----------------------------------------------------------------------

    private (string Org, string Project, string Repo)? ParseRepoUrl()
    {
        // Expected: https://dev.azure.com/{org}/{project}/_git/{repo}
        var url = _settings.KeyVaultRepoUrl?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) return null;

        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            // segments: [org, project, _git, repo]
            if (segments.Length >= 4 && segments[2] == "_git")
                return (segments[0], segments[1], segments[3]);
        }
        // must-stay-silent: malformed URL means user hasn't configured the
        // DevOps vault yet (or typoed it). Returning null lets the caller
        // surface a friendly "configure DevOps vault" message; logging here
        // would fire on every settings load before configuration.
        catch { }
        return null;
    }

    private async Task<string?> GetDevOpsTokenAsync()
    {
        var result = await _devOpsAuth.TryAcquireTokenSilentAsync();
        return result?.AccessToken;
    }

    /// <summary>Normalises a Key Vault repo URL for trust comparison (trimmed, no trailing slash, lower-cased).</summary>
    private static string NormalizeKeyVaultUrl(string? url)
        => (url ?? string.Empty).Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>
    /// SEC-1 (2026-07 audit). Issues a machine-bound trust token for
    /// <paramref name="url"/> — called from Settings when the user approves a
    /// Key Vault URL. The token is a DPAPI envelope (CurrentUser + per-app
    /// entropy) of the normalised URL, persisted to
    /// <see cref="AppSettings.KeyVaultRepoUrlHash"/>.
    /// <para>
    /// Unlike the previous plain <c>SHA256(url)</c>, this cannot be forged by an
    /// attacker who can only write <c>settings.json</c>: producing a valid token
    /// for a different URL requires calling <c>ProtectedData.Protect</c> as the
    /// current user (i.e. code execution as that user — at which point the keys
    /// are already reachable and the guard is moot). The control now matches its
    /// documented purpose: defending against a malicious settings.json edit /
    /// config sync silently redirecting key pushes.
    /// </para>
    /// </summary>
    public static string IssueKeyVaultTrustToken(string? url)
    {
        var norm = NormalizeKeyVaultUrl(url);
        return norm.Length == 0 ? string.Empty : SecretProtection.Encrypt(norm);
    }

    /// <summary>
    /// SEC-1. Returns true only when <paramref name="storedToken"/> is an
    /// authentic DPAPI trust token (see <see cref="IssueKeyVaultTrustToken"/>)
    /// whose protected value equals the normalised <paramref name="url"/>. A
    /// legacy plain-hash, an attacker-written plaintext, a forged/tampered
    /// token, or a token minted by another user all fail — forcing a one-time
    /// re-approval in Settings. Uses <see cref="SecretProtection.DecryptAuthentic"/>
    /// so a non-DPAPI value can never be accepted.
    /// </summary>
    public static bool IsKeyVaultUrlTrusted(string? url, string? storedToken)
    {
        var norm = NormalizeKeyVaultUrl(url);
        if (norm.Length == 0) return false;

        // D3: machine-mandated vault URL bypasses per-user TOFU — HKLM
        // requires local admin. HKCU-mandated never bypasses (see
        // UpdateService.IsFeedTrusted for the full rationale).
        if (Policy.PolicyService.Current.MachineMandatedEquals("KeyVaultRepoUrl", url))
            return true;

        if (string.IsNullOrEmpty(storedToken)) return false;
        var approved = SecretProtection.DecryptAuthentic(storedToken);
        return !string.IsNullOrEmpty(approved)
            && string.Equals(approved, norm, StringComparison.Ordinal);
    }

    /// <summary>
    /// Thrown by <see cref="PushToDevOpsAsync"/> when the current
    /// <see cref="AppSettings.KeyVaultRepoUrl"/> doesn't match the stored
    /// TOFU hash. Caller should surface a confirmation dialog and store the
    /// approved URL hash on user consent (see SettingsViewModel).
    /// </summary>
    public sealed class KeyVaultUrlNotTrustedException : Exception
    {
        public string Url { get; }
        public KeyVaultUrlNotTrustedException(string url)
            : base($"The Key Vault URL '{url}' has not been approved on this machine. " +
                   "Approve it in Settings → Key Vault before pushing keys.")
        {
            Url = url;
        }
    }

    /// <summary>
    /// Thrown by <see cref="SaveKeysAsync"/> when
    /// <see cref="AppSettings.KeyVaultRepoUrl"/> is empty. Wrapp stores
    /// encryption keys exclusively in the DevOps vault, so there is no
    /// fallback once the vault is unconfigured -- the keys are lost. The
    /// caller surfaces a dialog or prominent warn so the user knows to
    /// configure the vault and re-package.
    /// </summary>
    public sealed class DevOpsVaultNotConfiguredException : Exception
    {
        public DevOpsVaultNotConfiguredException()
            : base("Encryption keys cannot be saved -- the Azure DevOps key vault is not configured. "
                 + "Configure it in Settings -> Key Vault before saving keys.") { }

        /// <summary>
        /// Phase 16b. Built from <see cref="IFeatureGate.DescribeWhyDisabled"/>
        /// so the dialog text matches the actual gate state (URL missing vs.
        /// checkbox unchecked) instead of always pointing at &ldquo;configure URL&rdquo;.
        /// Falls back to the parameterless message when <paramref name="reason"/>
        /// is null or empty.
        /// </summary>
        public DevOpsVaultNotConfiguredException(string? reason)
            : base(string.IsNullOrEmpty(reason)
                ? "Encryption keys cannot be saved -- the Azure DevOps key vault is not configured. "
                  + "Configure it in Settings -> Key Vault before saving keys."
                : $"Encryption keys cannot be saved -- {reason}") { }
    }

    private async Task PushToDevOpsAsync(EncryptionKeyInfo keys, bool isManual = false)
    {
        // TOFU: refuse to push if the current URL doesn't match the approved
        // hash. This prevents a settings.json tamper (or an unvetted config
        // import) from silently redirecting key pushes to an attacker-controlled
        // DevOps org.
        if (!IsKeyVaultUrlTrusted(_settings.KeyVaultRepoUrl, _settings.KeyVaultRepoUrlHash))
        {
            throw new KeyVaultUrlNotTrustedException(_settings.KeyVaultRepoUrl ?? string.Empty);
        }

        var parsed = ParseRepoUrl();
        if (parsed is null) return;
        var (org, project, repo) = parsed.Value;

        var token = await GetDevOpsTokenAsync();
        if (token is null) throw new InvalidOperationException("Could not acquire DevOps token");

        // Phase 16c. Path is operator-configurable via two settings templates
        // (auto vs. manual). VaultPathTemplate.Resolve sanitises each token
        // value so user-controlled fields (AppName, PackageName) cannot
        // introduce path traversal or extra folder segments.
        var template = isManual
            ? _settings.KeyVaultManualPathTemplate
            : _settings.KeyVaultPathTemplate;
        var filePath = VaultPathTemplate.Resolve(template, keys);
        var content = JsonSerializer.Serialize(keys, JsonOpts);
        var contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        // Check if file already exists (need the latest commit for the ref update)
        var apiBase = $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}";

        // Get default branch ref
        var refsUrl = $"{apiBase}/refs?filter=heads/main&api-version=7.0";
        var request = new HttpRequestMessage(HttpMethod.Get, refsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var refsResp = await Http.SendAsync(request);
        var refsJson = await refsResp.Content.ReadAsStringAsync();

        // Parse the latest commit ID from refs
        string oldObjectId = "0000000000000000000000000000000000000000"; // new file
        try
        {
            using var doc = JsonDocument.Parse(refsJson);
            var refs = doc.RootElement.GetProperty("value");
            if (refs.GetArrayLength() > 0)
                oldObjectId = refs[0].GetProperty("objectId").GetString() ?? oldObjectId;
        }
        // Expected for a brand-new empty repo (no refs yet): the all-zeros
        // oldObjectId is the documented "create first commit" sentinel.
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        // STA-4 (2026-07 audit): a genuinely malformed response (schema change,
        // an auth error page) is NOT the empty-repo case -- log it so a push
        // against a bogus base commit is diagnosable instead of silent.
        catch (Exception ex)
        {
            AppLogger.Warn($"KeyStore: unexpected refs response, proceeding with sentinel base -- {ex.Message}");
        }

        // Create push (create/update file)
        var pushBody = new
        {
            refUpdates = new[] { new { name = "refs/heads/main", oldObjectId } },
            commits = new[]
            {
                new
                {
                    comment = $"Save encryption keys for {keys.DisplayName} ({keys.AppId})",
                    changes = new[]
                    {
                        new
                        {
                            changeType = "add", // "add" works for both create and update
                            item = new { path = filePath },
                            newContent = new { content = contentBase64, contentType = "base64encoded" }
                        }
                    }
                }
            }
        };

        var pushUrl = $"{apiBase}/pushes?api-version=7.0";
        var pushRequest = new HttpRequestMessage(HttpMethod.Post, pushUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(pushBody), Encoding.UTF8, "application/json")
        };
        pushRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var pushResp = await Http.SendAsync(pushRequest);
        ThrowIfSignInPage(pushResp, "push");
        if (!pushResp.IsSuccessStatusCode)
        {
            var err = await pushResp.Content.ReadAsStringAsync();
            // If "add" fails because file exists, retry with "edit"
            if (err.Contains("exists", StringComparison.OrdinalIgnoreCase))
            {
                var editBody = new
                {
                    refUpdates = new[] { new { name = "refs/heads/main", oldObjectId } },
                    commits = new[]
                    {
                        new
                        {
                            comment = $"Update encryption keys for {keys.DisplayName} ({keys.AppId})",
                            changes = new[]
                            {
                                new
                                {
                                    changeType = "edit",
                                    item = new { path = filePath },
                                    newContent = new { content = contentBase64, contentType = "base64encoded" }
                                }
                            }
                        }
                    }
                };
                var editRequest = new HttpRequestMessage(HttpMethod.Post, pushUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(editBody), Encoding.UTF8, "application/json")
                };
                editRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var editResp = await Http.SendAsync(editRequest);
                ThrowIfSignInPage(editResp, "push");
                if (!editResp.IsSuccessStatusCode)
                    throw new HttpRequestException($"DevOps push failed: {await editResp.Content.ReadAsStringAsync()}");
            }
            else
            {
                throw new HttpRequestException($"DevOps push failed: {err}");
            }
        }
    }

    /// <summary>
    /// Collapses empty path segments in a git ref name so a template token
    /// that resolved to an empty string (e.g. <c>{AppId}</c> on a manual save)
    /// can't produce an invalid ref like <c>wrapp-keys/2026-07-20/</c> (trailing
    /// slash) or <c>a//b</c>. Splitting on '/' with RemoveEmptyEntries then
    /// re-joining drops the empties while preserving the folder structure.
    /// </summary>
    internal static string SanitizeGitRef(string name) =>
        string.Join("/", name.Split('/', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// STA-5 (found 2026-07-29 during Workstream D): Azure DevOps answers an
    /// unauthenticated/underscoped API call with a sign-in page delivered as a
    /// "success" — either 203 NonAuthoritativeInformation, or a redirect chain
    /// that lands on the login page as a plain 200 text/html. Both pass
    /// IsSuccessStatusCode, so a key push with a rejected token previously
    /// returned CLEANLY without saving — and the DevOps vault is the ONLY key
    /// store, so that is silent key loss. A real pushes/pullrequests response
    /// is application/json; anything HTML-shaped on a 2xx is the auth wall.
    /// Fail loudly instead.
    /// </summary>
    private static void ThrowIfSignInPage(HttpResponseMessage resp, string operation)
    {
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        var htmlOnSuccess = resp.IsSuccessStatusCode
            && string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase);
        if ((int)resp.StatusCode == 203 || htmlOnSuccess)
        {
            AppLogger.Warn($"KeyStore: DevOps {operation} returned {(int)resp.StatusCode} {mediaType} (sign-in page) -- token rejected, nothing was saved");
            throw new HttpRequestException(
                $"DevOps {operation} failed: authentication was rejected (received the sign-in page instead of an API response). " +
                "Reconnect Azure DevOps from the account menu and try again.");
        }
    }

    /// <summary>
    /// Phase 16d. PR-mode counterpart to <see cref="PushToDevOpsAsync"/>:
    /// creates a feature branch from <c>main</c>&#x2019;s tip, commits the
    /// key file onto it, then opens a Pull Request targeting <c>main</c>.
    /// For organisations that disallow direct main commits in their key
    /// vault repo. Templates for the branch name, PR title, and PR
    /// description all expand via <see cref="VaultPathTemplate.Resolve"/>
    /// so the operator can match their team&#x2019;s naming conventions.
    /// <para>
    /// One PR per save (no batching): each <see cref="SaveKeysAsync"/>
    /// produces a distinct branch + PR so the review trail per key stays
    /// clean. Batched saves from Tools&nbsp;&gt;&nbsp;Inspect produce N
    /// PRs &mdash; the reviewer&#x2019;s convenience is the operator&#x2019;s
    /// problem if they want it.
    /// </para>
    /// </summary>
    private async Task PushAsPullRequestAsync(EncryptionKeyInfo keys, bool isManual = false)
    {
        // Same TOFU guard as PushToDevOpsAsync.
        if (!IsKeyVaultUrlTrusted(_settings.KeyVaultRepoUrl, _settings.KeyVaultRepoUrlHash))
        {
            throw new KeyVaultUrlNotTrustedException(_settings.KeyVaultRepoUrl ?? string.Empty);
        }

        var parsed = ParseRepoUrl();
        if (parsed is null) return;
        var (org, project, repo) = parsed.Value;

        // Resolve all templates up front so an empty / typo'd template fails
        // BEFORE any MSAL or HTTP work. Cheaper feedback for the operator and
        // a stable test seam that doesn't depend on an HTTP mock.
        var pathTemplate = isManual
            ? _settings.KeyVaultManualPathTemplate
            : _settings.KeyVaultPathTemplate;
        var filePath = VaultPathTemplate.Resolve(pathTemplate, keys);

        // Manual saves (Inspect tab) have no Intune {AppId}, so the default
        // branch template "wrapp-keys/{Date}/{AppId}" would resolve to
        // "wrapp-keys/{Date}/" -- a trailing-slash segment that is NOT a valid
        // git ref (DevOps rejects the refUpdate). Substitute {PackageName} for
        // {AppId} in that case so the branch keeps a real, unique final segment.
        var branchTemplate = isManual
            ? _settings.KeyVaultPrSourceBranchTemplate.Replace("{AppId}", "{PackageName}")
            : _settings.KeyVaultPrSourceBranchTemplate;
        // Collapse any empty path segments left by an empty token so the result
        // is always a valid ref name (no '//' or trailing '/').
        var sourceBranch = SanitizeGitRef(VaultPathTemplate.Resolve(branchTemplate, keys));
        var prTitle = VaultPathTemplate.Resolve(_settings.KeyVaultPrTitleTemplate, keys);
        var prDescription = VaultPathTemplate.Resolve(_settings.KeyVaultPrDescriptionTemplate, keys);

        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(sourceBranch) || string.IsNullOrEmpty(prTitle))
            throw new InvalidOperationException(
                "PR-mode templates resolved to empty -- check Settings -> Key Vault.");

        var token = await GetDevOpsTokenAsync()
            ?? throw new InvalidOperationException("Could not acquire DevOps token");

        var apiBase = $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}";

        // 1. GET main's tip. The new branch is created from this commit.
        const string ZeroObjectId = "0000000000000000000000000000000000000000";
        string mainTip = ZeroObjectId;
        using (var refsReq = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBase}/refs?filter=heads/main&api-version=7.0"))
        {
            refsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var refsResp = await Http.SendAsync(refsReq);
            var refsJson = await refsResp.Content.ReadAsStringAsync();
            // A non-success status here is NOT the empty-repo case -- it's an
            // auth / repo-URL / permission failure. Surface it directly (with
            // the server body) instead of proceeding to a doomed push whose
            // error would be far more opaque.
            if (!refsResp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"DevOps refs lookup failed ({(int)refsResp.StatusCode} {refsResp.StatusCode}) "
                    + $"for vault repo '{repo}'. Verify the repo URL and that the DevOps token has "
                    + $"Code (Read & Write) access. Server: {refsJson}");
            try
            {
                using var doc = JsonDocument.Parse(refsJson);
                var refs = doc.RootElement.GetProperty("value");
                if (refs.GetArrayLength() > 0)
                    mainTip = refs[0].GetProperty("objectId").GetString() ?? mainTip;
            }
            // A well-formed response with no 'main' ref leaves mainTip at the
            // zero sentinel -- handled explicitly below (PR mode can't seed one).
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }
            // STA-4: an unexpected response shape is logged, not swallowed.
            catch (Exception ex)
            {
                AppLogger.Warn($"KeyStore: unexpected refs response (PR mode), proceeding with sentinel base -- {ex.Message}");
            }
        }

        // PR mode requires an existing 'main' to branch from AND to target.
        // Unlike direct-push mode (which seeds an empty repo via the zero
        // sentinel), a PR against a non-existent main is impossible -- and a
        // create-branch push with newObjectId = all-zeros is rejected by DevOps
        // with an opaque error. Fail early with an actionable message.
        if (mainTip == ZeroObjectId)
            throw new InvalidOperationException(
                $"The vault repo '{repo}' has no 'main' branch, which pull-request mode requires. "
                + "Initialise it first -- commit any file to main in DevOps, or temporarily turn off "
                + "\"Use pull requests\" in Settings -> Key Vault so the next push creates main -- then retry.");

        // 2. Create the feature branch AND commit the file in one push call.
        //    refUpdates.oldObjectId = all-zeros means "create this ref"; the
        //    branch's first commit becomes mainTip's tree + our changes.
        var content = JsonSerializer.Serialize(keys, JsonOpts);
        var contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var pushBody = new
        {
            // Create the branch by basing the new commit on main's tip:
            // oldObjectId = the parent commit, and the service computes the new
            // commit id from `commits`. The earlier form (oldObjectId all-zeros
            // + an explicit newObjectId ALONGSIDE commits) is the "combination
            // of parameters is not valid -- Parameter name: refUpdate" that
            // DevOps rejected, so PR-mode saves never succeeded.
            refUpdates = new[]
            {
                new
                {
                    name = $"refs/heads/{sourceBranch}",
                    oldObjectId = mainTip,
                }
            },
            commits = new[]
            {
                new
                {
                    comment = prTitle,
                    changes = new[]
                    {
                        new
                        {
                            changeType = "add",
                            item = new { path = filePath },
                            newContent = new { content = contentBase64, contentType = "base64encoded" }
                        }
                    }
                }
            }
        };

        using (var pushReq = new HttpRequestMessage(HttpMethod.Post,
            $"{apiBase}/pushes?api-version=7.0"))
        {
            pushReq.Content = new StringContent(
                JsonSerializer.Serialize(pushBody), Encoding.UTF8, "application/json");
            pushReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var pushResp = await Http.SendAsync(pushReq);
            ThrowIfSignInPage(pushResp, "branch push");
            if (!pushResp.IsSuccessStatusCode)
            {
                var err = await pushResp.Content.ReadAsStringAsync();
                // Log to app.log (parity with the PR-creation step below). This
                // step previously threw with NO AppLogger call, so a branch-push
                // failure left no persisted trace -- only the transient run-log
                // pane -- and the DevOps reason was unrecoverable after the run.
                AppLogger.Warn(
                    $"KeyStore: branch + commit push failed for app '{keys.AppId}' "
                    + $"(branch '{sourceBranch}'). Server: {err}");
                throw new HttpRequestException(
                    $"DevOps branch + push failed (branch '{sourceBranch}'): {err}");
            }
        }

        // 3. Create the PR. If this step fails the commit is already on the
        //    branch -- log the branch name so the operator can open the PR
        //    manually from the DevOps web UI.
        var prBody = new
        {
            sourceRefName = $"refs/heads/{sourceBranch}",
            targetRefName = "refs/heads/main",
            title = prTitle,
            description = prDescription,
        };

        using (var prReq = new HttpRequestMessage(HttpMethod.Post,
            $"{apiBase}/pullrequests?api-version=7.0"))
        {
            prReq.Content = new StringContent(
                JsonSerializer.Serialize(prBody), Encoding.UTF8, "application/json");
            prReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var prResp = await Http.SendAsync(prReq);
            ThrowIfSignInPage(prResp, "PR creation");
            if (!prResp.IsSuccessStatusCode)
            {
                var err = await prResp.Content.ReadAsStringAsync();
                AppLogger.Warn(
                    $"KeyStore: PR creation failed for branch '{sourceBranch}'. "
                    + $"The commit was pushed; open the PR manually from the DevOps web UI. Server: {err}");
                throw new HttpRequestException(
                    $"PR creation failed (commit succeeded on branch '{sourceBranch}'): {err}");
            }
        }
    }

    // Phase 16c: read path uses the legacy hard-coded layout as a fast
    // single-HTTP lookup. Keys written under a custom KeyVaultPathTemplate
    // miss this path and are picked up by LoadAllDevOpsKeysAsync's recursive
    // walk instead (callers cascade to brute-force when GetKeysAsync returns
    // null). Templating the read path would require also knowing the
    // template that produced each on-disk key, which the operator can
    // change retroactively -- the brute-force fallback is the safer design.
    private async Task<EncryptionKeyInfo?> FetchFromDevOpsAsync(string tenantId, string appId)
    {
        // SEC-1: guard reads with the same trust check as pushes.
        if (!IsKeyVaultUrlTrusted(_settings.KeyVaultRepoUrl, _settings.KeyVaultRepoUrlHash))
        {
            AppLogger.Warn("KeyStore: vault URL not approved -- skipping fetch. Approve it in Settings -> Key Vault.");
            return null;
        }

        var parsed = ParseRepoUrl();
        if (parsed is null) return null;
        var (org, project, repo) = parsed.Value;

        var token = await GetDevOpsTokenAsync();
        if (token is null) return null;

        var filePath = $"/wrapp/{tenantId}/{appId}.json";
        var url = $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}/items?path={Uri.EscapeDataString(filePath)}&api-version=7.0";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        var resp = await Http.SendAsync(request);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<EncryptionKeyInfo>(json);
    }

    private async Task<bool> CheckDevOpsExistsAsync(string tenantId, string appId)
    {
        // SEC-1: guard reads with the same trust check as pushes.
        if (!IsKeyVaultUrlTrusted(_settings.KeyVaultRepoUrl, _settings.KeyVaultRepoUrlHash))
            return false;

        var parsed = ParseRepoUrl();
        if (parsed is null) return false;
        var (org, project, repo) = parsed.Value;

        var token = await GetDevOpsTokenAsync();
        if (token is null) return false;

        var filePath = $"/wrapp/{tenantId}/{appId}.json";
        var url = $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}/items?path={Uri.EscapeDataString(filePath)}&api-version=7.0";

        var request = new HttpRequestMessage(HttpMethod.Head, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await Http.SendAsync(request);
        return resp.IsSuccessStatusCode;
    }
}
