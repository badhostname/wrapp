using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Intune content-download path for <see cref="AppInventoryService"/> --
/// raw <c>.intunewin</c> blob fetch via Graph API + AES-CBC decryption to
/// recover the inner package contents. Moved into a partial file so the
/// core class focuses on cache + Graph metadata orchestration.
///
/// <para>Pure helpers (RFC4648 base64-url decode, HMAC verification) live
/// here too because they are only called from these two methods.</para>
/// </summary>
public partial class AppInventoryService
{
    // -----------------------------------------------------------------------
    // Content download + decryption
    // -----------------------------------------------------------------------

    /// <summary>
    /// Downloads the raw .intunewin blob (no decryption) to the specified output path.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> DownloadRawContentAsync(
        string tenantId, string appId, string outputPath,
        IProgress<string>? progress)
    {
        var token = await GetTokenAsync(tenantId);
        if (token is null) return false;

        progress?.Report("Connecting to Intune...");
        AppLogger.Info($"Inventory: downloading raw content for app {appId}");

        // Do the content version resolution in C# where HTTP error handling is reliable.
        // Invoke-RestMethod in hosted SMA throws .NET terminating errors on 404 that
        // bypass both -ErrorAction SilentlyContinue and PS try/catch.
        try
        {
            // Graph API calls -- authenticated with Bearer token
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);

            // Azure Blob Storage downloads -- MUST NOT send Bearer auth.
            // The SAS token in the URL query string is the authentication;
            // adding an Authorization header causes Azure Storage to return 403.
            using var blobHttp = new System.Net.Http.HttpClient();

            var baseUrl = "https://graph.microsoft.com/beta";

            // 1. Get app metadata (retried on 429/503 with Retry-After)
            progress?.Report("Fetching app metadata...");
            using var appResp = await GraphRetryPolicy.SendAsync(
                c => http.GetAsync($"{baseUrl}/deviceAppManagement/mobileApps/{appId}", c),
                log: AppLogger.Info);
            appResp.EnsureSuccessStatusCode();
            var appJson = await appResp.Content.ReadAsStringAsync();
            var appDoc = System.Text.Json.JsonDocument.Parse(appJson);
            var odataType   = appDoc.RootElement.GetStringOr("@odata.type");
            var contentVer  = appDoc.RootElement.GetStringOr("committedContentVersion");
            var displayName = appDoc.RootElement.GetStringOr("displayName");

            AppLogger.Info($"Inventory: download resolve -- {displayName}: odata.type={odataType}, committedContentVersion={contentVer}");

            if (string.IsNullOrEmpty(contentVer))
            {
                AppLogger.Warn($"Inventory: no committedContentVersion for {displayName} (odata.type={odataType})");
                progress?.Report($"Download failed: no committed content version (type={odataType})");
                return false;
            }

            // 2. Determine type cast
            var typeCast = odataType switch
            {
                _ when odataType.Contains("win32LobApp") => "microsoft.graph.win32LobApp",
                _ when odataType.Contains("windowsMobileMSI") => "microsoft.graph.windowsMobileMSI",
                _ when odataType.Contains("iosLobApp") => "microsoft.graph.iosLobApp",
                _ when odataType.Contains("androidLobApp") => "microsoft.graph.androidLobApp",
                _ when odataType.Contains("windowsUniversalAppX") => "microsoft.graph.windowsUniversalAppX",
                _ when odataType.Contains("windowsAppX") => "microsoft.graph.windowsAppX",
                _ when odataType.Contains("microsoftStoreForBusinessApp") => "microsoft.graph.microsoftStoreForBusinessApp",
                _ => "microsoft.graph.mobileLobApp"
            };

            // 3. Enumerate content versions and try each file until a download succeeds.
            // IMPORTANT: The azureStorageUriExpirationDateTime is often stale/wrong --
            // the URI actually works regardless. So we try the actual download rather
            // than pre-filtering on the expiry metadata.
            progress?.Report("Resolving content version...");
            long fileSize = 0;
            string usedVersion = contentVer;

            var versionsUrl = $"{baseUrl}/deviceAppManagement/mobileApps/{appId}/{typeCast}/contentVersions";
            var versionsResp = await GraphRetryPolicy.SendAsync(c => http.GetAsync(versionsUrl, c), log: AppLogger.Info);

            var versionIds = new List<string>();
            if (versionsResp.IsSuccessStatusCode)
            {
                var versionsJson = await versionsResp.Content.ReadAsStringAsync();
                var versionsDoc = System.Text.Json.JsonDocument.Parse(versionsJson);
                if (versionsDoc.RootElement.TryGetProperty("value", out var versionsArr))
                {
                    foreach (var v in versionsArr.EnumerateArray())
                    {
                        if (v.TryGetProperty("id", out var vid))
                            versionIds.Add(vid.GetString() ?? "");
                    }
                }
            }
            // Put the committed version first, then the rest sorted descending
            versionIds = versionIds
                .OrderBy(v => v == contentVer ? 0 : 1)
                .ThenByDescending(v => int.TryParse(v, out var n) ? n : 0)
                .ToList();

            AppLogger.Info($"Inventory: {displayName} has {versionIds.Count} content version(s): [{string.Join(", ", versionIds)}], committed={contentVer}");

            // Walk each version, try each file's Azure URI as an actual download
            bool downloaded = false;
            foreach (var vid in versionIds)
            {
                var filesUrl = $"{baseUrl}/deviceAppManagement/mobileApps/{appId}/{typeCast}/contentVersions/{vid}/files";
                var filesResp = await GraphRetryPolicy.SendAsync(c => http.GetAsync(filesUrl, c), log: AppLogger.Info);
                if (!filesResp.IsSuccessStatusCode) continue;

                var filesJson = await filesResp.Content.ReadAsStringAsync();
                var filesDoc = System.Text.Json.JsonDocument.Parse(filesJson);
                if (!filesDoc.RootElement.TryGetProperty("value", out var filesArr) || filesArr.GetArrayLength() == 0)
                    continue;

                AppLogger.Info($"Inventory: version {vid} has {filesArr.GetArrayLength()} file(s)");

                // Try each file entry in this version (last first)
                for (int i = filesArr.GetArrayLength() - 1; i >= 0; i--)
                {
                    var fileEntry = filesArr[i];
                    var fileId   = fileEntry.GetStringOr("id");
                    var fileName = fileEntry.GetStringOr("name");

                    var fileUrl = $"{baseUrl}/deviceAppManagement/mobileApps/{appId}/{typeCast}/contentVersions/{vid}/files/{fileId}";
                    var fileResResp = await GraphRetryPolicy.SendAsync(c => http.GetAsync(fileUrl, c), log: AppLogger.Info);
                    if (!fileResResp.IsSuccessStatusCode)
                    {
                        AppLogger.Info($"Inventory: v{vid}/file {fileId} ({fileName}) returned {(int)fileResResp.StatusCode}");
                        continue;
                    }

                    var fileResJson = await fileResResp.Content.ReadAsStringAsync();
                    var fileResDoc = System.Text.Json.JsonDocument.Parse(fileResJson);

                    var uri = fileResDoc.RootElement.GetStringOr("azureStorageUri");
                    var sz  = fileResDoc.RootElement.GetInt64Or("size");

                    if (string.IsNullOrEmpty(uri))
                    {
                        AppLogger.Info($"Inventory: v{vid}/file {fileId} ({fileName}) has no azureStorageUri");
                        continue;
                    }

                    // Try the download. The SAS URI often works even when the expiry metadata says otherwise.
                    AppLogger.Info($"Inventory: attempting download from v{vid}/file {fileId} ({fileName})");
                    progress?.Report($"Downloading {fileName} from Azure storage...");

                    var blobResp = await blobHttp.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    if (blobResp.IsSuccessStatusCode)
                    {
                        using var blobStream = await blobResp.Content.ReadAsStreamAsync();
                        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                        await blobStream.CopyToAsync(fileStream);
                        var downloadedSize = fileStream.Length;
                        fileSize = sz;
                        usedVersion = vid;
                        AppLogger.Info($"Inventory: downloaded raw .intunewin ({downloadedSize:N0} bytes) to {outputPath} (version={vid}, file={fileName})");
                        progress?.Report("Download complete.");
                        downloaded = true;
                        break;
                    }

                    var blobStatus = (int)blobResp.StatusCode;
                    AppLogger.Info($"Inventory: v{vid}/file {fileId} blob download returned {blobStatus}, trying renewUpload...");

                    // Blob download failed -- attempt renewUpload and retry once
                    var renewUrl = $"{baseUrl}/deviceAppManagement/mobileApps/{appId}/{typeCast}/contentVersions/{vid}/files/{fileId}/renewUpload";
                    var renewResp = await GraphRetryPolicy.SendAsync(c => http.PostAsync(renewUrl, null, c), log: AppLogger.Info);
                    AppLogger.Info($"Inventory: renewUpload for v{vid}/file {fileId}: {(int)renewResp.StatusCode}");

                    if (!renewResp.IsSuccessStatusCode) continue;

                    // Re-fetch the file resource to pick up the renewed URI
                    var refreshResp = await GraphRetryPolicy.SendAsync(c => http.GetAsync(fileUrl, c), log: AppLogger.Info);
                    if (!refreshResp.IsSuccessStatusCode) continue;

                    var refreshJson = await refreshResp.Content.ReadAsStringAsync();
                    var refreshDoc = System.Text.Json.JsonDocument.Parse(refreshJson);
                    var newUri = refreshDoc.RootElement.GetStringOr("azureStorageUri");
                    if (string.IsNullOrEmpty(newUri)) continue;

                    var retryResp = await blobHttp.GetAsync(newUri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    if (retryResp.IsSuccessStatusCode)
                    {
                        using var retryStream = await retryResp.Content.ReadAsStreamAsync();
                        using var retryFile = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                        await retryStream.CopyToAsync(retryFile);
                        var retrySize = retryFile.Length;
                        fileSize = sz;
                        usedVersion = vid;
                        AppLogger.Info($"Inventory: downloaded after renewUpload ({retrySize:N0} bytes) (version={vid}, file={fileName})");
                        progress?.Report("Download complete.");
                        downloaded = true;
                        break;
                    }

                    AppLogger.Info($"Inventory: v{vid}/file {fileId} retry after renewUpload also returned {(int)retryResp.StatusCode}");
                }

                if (downloaded) break;
            }

            if (!downloaded)
            {
                AppLogger.Warn($"Inventory: no downloadable content found for {displayName} (committed={contentVer}, type={typeCast})");
                progress?.Report("Download failed: content no longer available in Azure storage. The app may need to be re-uploaded in Intune.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Inventory: raw content download failed for app {appId} -- [{ex.GetType().Name}] {ex.Message}");
            if (ex.StackTrace is not null)
                AppLogger.Warn($"Inventory: stack trace -- {ex.StackTrace.Split('\n')[0].Trim()}");
            progress?.Report($"Download failed: {ex.Message}");
            return false;
        }
    }

}
