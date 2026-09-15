using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class InventoryViewModel
{
    // -----------------------------------------------------------------------
    // Import-to-Wrapp workflow -- creates a fresh bundle workspace from an
    // Intune or SCCM inventory entry. Includes the per-content-type
    // analysis + placement helpers and the Intune/SCCM package mappers.
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task ImportToWrappAsync()
    {
        if (AppDetail is null || _generalVm is null) return;

        if (_generalVm.IsActiveBundle)
        {
            StatusText = "Cannot import on an active bundle. Create a new bundle first.";
            AppLogger.Warn("Inventory: import rejected -- active bundle");
            return;
        }

        var detail = AppDetail;
        var tenantId = SelectedTarget?.Key;

        var hasKeys = false;
        if (_keyStore is not null && !string.IsNullOrEmpty(tenantId))
        {
            try { hasKeys = await _keyStore.HasKeysAsync(tenantId, detail.Id); }
            catch { /* ignore -- will show as no keys */ }
        }

        var sizeLabel = detail.SizeInBytes > 0 ? $" (~{detail.SizeDisplay})" : "";
        var keysLabel = hasKeys ? " - encryption keys found" : " - no encryption keys";
        var cloneDesc = hasKeys
            ? "Downloads and decrypts the app's content using stored encryption keys. The original installer will be extracted and placed in the bundle."
            : "Downloads the app's encrypted content from Intune. Without encryption keys the content cannot be decrypted. You can provide keys manually after download.";

        var options = new ActionPickerOption[]
        {
            new()
            {
                Key = "metadata",
                Icon = "\uE8A5", // Document
                Title = "Import Metadata Only",
                Description = "Creates a new bundle with app info, assignments, dependencies, icon, and detection rules from Intune. No installer content is downloaded."
            },
            new()
            {
                Key = "fullclone",
                Icon = "\uE896", // Download
                Title = $"Full Clone{sizeLabel}{keysLabel}",
                Description = cloneDesc,
                IsEnabled = detail.Platform == AppPlatform.Intune && !string.IsNullOrEmpty(tenantId),
                DisabledReason = detail.Platform != AppPlatform.Intune
                    ? "Full clone is only available for Intune apps"
                    : "Select a tenant first"
            },
        };

        var dialog = new Views.ActionPickerDialog(
            $"Import '{detail.DisplayName}' to a new Wrapp bundle.", options, defaultKey: "metadata");

        var confirmed = await FluentDialog.ShowSelectAsync(
            "Import to Wrapp", dialog, "Import", "Cancel");
        if (!confirmed) return;

        var fullClone = dialog.SelectedKey == "fullclone";

        // Build a step tree for the Background Jobs popup. Each phase
        // transitions Pending -> Running -> Succeeded/Failed/Skipped so
        // the user sees progress in real time.
        var stepTree    = new JobStepTree();
        var stepMeta    = stepTree.Add("Build metadata");
        var stepSaveIcon = stepTree.Add("Save icon");
        JobStep? stepDownload = null;
        JobStep? stepDecrypt  = null;
        JobStep? stepDecEmb   = null;
        JobStep? stepDecVault = null;
        JobStep? stepDecBrute = null;
        JobStep? stepPopulate = null;
        if (fullClone)
        {
            stepDownload = stepTree.Add("Download .intunewin");
            stepDecrypt  = stepTree.Add("Decrypt content");
            stepDecEmb   = stepDecrypt.AddSubStep("Embedded keys (Detection.xml)");
            stepDecVault = stepDecrypt.AddSubStep("Vault lookup (tenant + app ID)");
            stepDecBrute = stepDecrypt.AddSubStep("Vault brute-force");
            stepPopulate = stepTree.Add("Populate bundle");
        }
        var stepOverlay = stepTree.Add("Overlay Intune metadata");
        var stepSave    = stepTree.Add("Save bundle");

        var importJob = _jobTracker?.BeginJob(
            $"Import: {detail.DisplayName}", bundleRoot: null, context: stepTree) ?? default;
        AppLogger.Info($"Inventory: importing '{detail.DisplayName}' ({detail.Platform}, {(fullClone ? "full clone" : "metadata only")})");

        try
        {
            stepMeta.Start();
            var config = new AppConfigModel();
            config.App.Name = detail.DisplayName;
            config.App.Company = detail.Publisher;
            config.App.DotVersion = detail.Version;
            config.App.Version = detail.Version.Replace(".", "_");
            config.App.ScriptFramework = "Appease";

            if (detail.Platform == AppPlatform.Intune)
                MapIntunePackage(config, detail);
            else
                MapSccmPackage(config, detail);

            var configPath = await TempWorkspaceService.CreateAsync();
            var bundleRoot = System.IO.Path.GetDirectoryName(
                System.IO.Path.GetDirectoryName(configPath))!;
            stepMeta.Finish(StepState.Succeeded, $"Workspace at {bundleRoot}");

            // Lazy-load icon if not yet fetched, then save to workspace
            stepSaveIcon.Start();
            if (string.IsNullOrEmpty(detail.IconBase64) && !string.IsNullOrEmpty(tenantId))
            {
                var iconData = await _inventoryService.FetchIconBase64Async(tenantId, detail.Id);
                if (!string.IsNullOrEmpty(iconData))
                    detail.IconBase64 = iconData;
            }
            SaveIconToWorkspace(detail, bundleRoot, config);
            stepSaveIcon.Finish(
                string.IsNullOrEmpty(detail.IconBase64) ? StepState.Skipped : StepState.Succeeded,
                string.IsNullOrEmpty(detail.IconBase64) ? "No icon available" : "Icon saved");

            // Full clone: download + decrypt + place content
            string keySource = "";
            if (fullClone && !string.IsNullOrEmpty(tenantId))
            {
                // ── Download ────────────────────────────────────────
                stepDownload!.Start();
                var tempIntunewin = System.IO.Path.Combine(bundleRoot, $"{FileNameSanitizer.Sanitize(detail.DisplayName)}.intunewin");
                StatusText = $"Downloading content for '{detail.DisplayName}'...";
                var downloadOk = await _inventoryService.DownloadRawContentAsync(
                    tenantId, detail.Id, tempIntunewin,
                    UiProgress.ForStatus(s => StatusText = s));

                if (!downloadOk || !System.IO.File.Exists(tempIntunewin))
                {
                    stepDownload.Finish(StepState.Failed, "Download returned no file");
                    stepDecrypt!.Finish(StepState.Skipped);
                    stepDecEmb!.Finish(StepState.Skipped);
                    stepDecVault!.Finish(StepState.Skipped);
                    stepDecBrute!.Finish(StepState.Skipped);
                    stepPopulate!.Finish(StepState.Skipped);
                    AppLogger.Warn("Inventory: content download returned no file, continuing with metadata only");
                }
                else
                {
                    stepDownload.Finish(StepState.Succeeded);

                    // ── Decrypt cascade ────────────────────────────
                    stepDecrypt!.Start();
                    var orchestrator = new IntuneWinDecryptOrchestrator(_keyStore!);
                    var tempDecryptDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wrapp_clone_{Guid.NewGuid():N}");
                    System.IO.Directory.CreateDirectory(tempDecryptDir);

                    var blob = await Task.Run(() => IntuneWinDecryptOrchestrator.PrepareBlob(tempIntunewin));
                    IntuneWinDecryptOrchestrator.DecryptResult? decResult = null;
                    var statusProgress = UiProgress.ForStatus(s => StatusText = s);

                    // 1. Embedded keys (Detection.xml inside the .intunewin)
                    stepDecEmb!.Start();
                    StatusText = "Trying embedded keys from Detection.xml...";
                    decResult = await orchestrator.DecryptWithEmbeddedKeysAsync(tempIntunewin, tempDecryptDir);
                    if (decResult?.Success == true)
                    {
                        keySource = "embedded keys (Detection.xml)";
                        stepDecEmb.Finish(StepState.Succeeded);
                        stepDecVault!.Finish(StepState.Skipped);
                        stepDecBrute!.Finish(StepState.Skipped);
                    }
                    else
                    {
                        stepDecEmb.Finish(StepState.Failed, "No match");

                        // 2. Vault lookup by tenant + app ID
                        if (hasKeys && !string.IsNullOrEmpty(tenantId))
                        {
                            stepDecVault!.Start();
                            StatusText = $"Trying vault key for tenant '{tenantId}'...";
                            decResult = await orchestrator.DecryptWithVaultAsync(
                                blob ?? tempIntunewin, tempDecryptDir, tenantId, detail.Id, status: statusProgress);
                            if (decResult?.Success == true)
                            {
                                keySource = $"vault lookup (tenant {tenantId}, app {detail.Id})";
                                stepDecVault.Finish(StepState.Succeeded);
                                stepDecBrute!.Finish(StepState.Skipped);
                            }
                            else
                            {
                                stepDecVault.Finish(StepState.Failed, "No matching key in vault");
                            }
                        }
                        else
                        {
                            stepDecVault!.Finish(StepState.Skipped, "No vault keys loaded");
                        }

                        // 3. Brute-force
                        if (decResult is null || !decResult.Success)
                        {
                            stepDecBrute!.Start();
                            StatusText = "Trying vault brute-force...";
                            decResult = await orchestrator.BruteForceDecryptAsync(
                                blob ?? tempIntunewin, tempDecryptDir, status: statusProgress);
                            if (decResult?.Success == true)
                            {
                                keySource = "vault brute-force";
                                stepDecBrute.Finish(StepState.Succeeded);
                            }
                            else
                            {
                                stepDecBrute.Finish(StepState.Failed, "No matching key");
                            }
                        }
                    }

                    // Clean up extracted blob temp if different from source
                    if (blob is not null && blob != tempIntunewin)
                        try { System.IO.File.Delete(blob); } catch { }

                    if (decResult?.Success == true && decResult.OutputPath is not null)
                    {
                        stepDecrypt.Finish(StepState.Succeeded, $"Matched via {keySource}");
                        AppLogger.Info($"Import-to-Wrapp: decrypt succeeded via {keySource}");
                        StatusText = $"Decrypted using {keySource}.";

                        // ── Populate bundle ─────────────────────────
                        stepPopulate!.Start();
                        await BundleService.PopulateFromDecryptedContentAsync(
                            decResult.OutputPath, bundleRoot, config, _settings!);
                        stepPopulate.Finish(StepState.Succeeded);

                        // ── Overlay the inner Config.json ──────────
                        // Honour the inner Config.json whenever it exists --
                        // the decrypted content's Config.json has just
                        // overwritten our temp-workspace stub, so
                        // FindConfigJson returning the same path as our
                        // initial stub is EXPECTED and must still load.
                        // (Earlier guard `configInBundle != configPath`
                        // caused EXEFile / MSIFile / IconFile to be wiped.)
                        var configInBundle = BundleService.FindConfigJson(bundleRoot);
                        if (configInBundle is not null)
                        {
                            var baseConfig = await ConfigFileService.LoadAsync(configInBundle);
                            baseConfig.App.Name      = detail.DisplayName;
                            baseConfig.App.Company   = detail.Publisher;
                            baseConfig.App.DotVersion = detail.Version;
                            baseConfig.App.Version   = detail.Version?.Replace(".", "_") ?? "";
                            if (detail.Platform == AppPlatform.Intune)
                                MapIntunePackage(baseConfig, detail);
                            config = baseConfig;
                            configPath = configInBundle;
                        }

                        // Fill-if-empty fallback: if the recovered config
                        // still has no EXEFile / MSIFile but the binary is
                        // physically in B/ or Files/, use that filename.
                        var (exeName, msiName) = BundleService.DetectInstallersInBinaryFolder(bundleRoot, config);
                        if (!string.IsNullOrEmpty(exeName) && string.IsNullOrEmpty(config.App.EXEFile))
                            config.App.EXEFile = exeName;
                        if (!string.IsNullOrEmpty(msiName) && string.IsNullOrEmpty(config.App.MSIFile))
                            config.App.MSIFile = msiName;

                        // Delete the raw .intunewin (we have the decrypted content now)
                        try { System.IO.File.Delete(tempIntunewin); } catch { }
                    }
                    else
                    {
                        stepDecrypt.Finish(StepState.Failed, "All auto-cascade sources failed");
                        stepPopulate!.Finish(StepState.Skipped);
                        AppLogger.Warn("Import-to-Wrapp: auto-cascade (embedded/vault/brute-force) failed to decrypt");
                        StatusText = "Could not decrypt content. Raw .intunewin saved in bundle.";

                        // Surface an escape hatch to Tools view for the
                        // user-input key sources (manual paste / CSV).
                        await FluentDialog.ShowInfoAsync(
                            "Decryption failed",
                            "Auto-cascade (embedded → vault → brute-force) could not decrypt the content. " +
                            "Use Tools → Intunewin Decrypt to try manual key/IV paste or a CSV key list.");
                    }

                    try { if (System.IO.Directory.Exists(tempDecryptDir)) System.IO.Directory.Delete(tempDecryptDir, true); } catch { }
                }
            }

            // ── Overlay + save ─────────────────────────────────────
            stepOverlay.Finish(StepState.Succeeded);  // Overlay happened inline above if clone; for metadata-only there's nothing to overlay.

            stepSave.Start();
            await ConfigFileService.SaveAsync(config, configPath);
            stepSave.Finish(StepState.Succeeded, System.IO.Path.GetFileName(configPath));

            _suppressRefreshOnConfigLoad = true;
            await _generalVm.LoadFromPathAsync(configPath, config);
            _suppressRefreshOnConfigLoad = false;

            var mode = fullClone ? "full clone" : "metadata";
            StatusText = $"Imported '{detail.DisplayName}' ({mode}) -- switch to Packages tab to review.";
            AppLogger.Info($"Inventory: import complete for '{detail.DisplayName}' ({mode})");
            importJob.Complete();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Inventory: import failed -- {ex.Message}");
            StatusText = $"Import failed: {ex.Message}";
            importJob.Fail(ex.Message);
        }
    }

    private static void SaveIconToWorkspace(AppInventoryDetail detail, string bundleRoot, AppConfigModel config)
    {
        if (string.IsNullOrEmpty(detail.IconBase64)) return;
        try
        {
            var iconDir = System.IO.Path.Combine(bundleRoot, "Icon");
            System.IO.Directory.CreateDirectory(iconDir);
            var iconName = FileNameSanitizer.Sanitize(detail.DisplayName);
            if (string.IsNullOrEmpty(iconName)) iconName = "AppIcon";
            var iconRelPath = System.IO.Path.Combine("Icon", $"{iconName}.png");
            var iconFullPath = System.IO.Path.Combine(bundleRoot, iconRelPath);
            var iconBytes = Convert.FromBase64String(detail.IconBase64);
            System.IO.File.WriteAllBytes(iconFullPath, iconBytes);
            config.App.IconFile = iconRelPath;
            var intunePkg = config.Script.IntunePackager.Packages.FirstOrDefault();
            if (intunePkg is not null) intunePkg.IconFile = iconRelPath;
            var sccmPkg = config.Script.SCCMPackager.Packages.FirstOrDefault();
            if (sccmPkg is not null) sccmPkg.Icon = iconRelPath;
            AppLogger.Info($"Inventory: saved icon to {iconFullPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Inventory: icon save failed -- {ex.Message}");
        }
    }

    /// <summary>Inspects a downloaded file to determine if it's a bundle, single file, or archive.</summary>
    private static ContentType AnalyzeContent(string filePath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(filePath);
            var entries = zip.Entries.Select(e => e.FullName).ToList();

            // Check for Appease bundle structure
            if (entries.Any(e => e.StartsWith("Script/", StringComparison.OrdinalIgnoreCase))
                && entries.Any(e => e.StartsWith("B/", StringComparison.OrdinalIgnoreCase)
                    || e.StartsWith("Shortcuts/", StringComparison.OrdinalIgnoreCase)))
            {
                return ContentType.AppeaseBundle;
            }

            // Check for PSADT bundle structure
            if (entries.Any(e => e.StartsWith("PSAppDeployToolkit/", StringComparison.OrdinalIgnoreCase)
                    || e.Contains("Invoke-AppDeployToolkit", StringComparison.OrdinalIgnoreCase))
                && entries.Any(e => e.StartsWith("Files/", StringComparison.OrdinalIgnoreCase)))
            {
                return ContentType.PsadtBundle;
            }

            // It's a valid ZIP but not a recognized bundle -- treat as compressed archive
            return ContentType.CompressedArchive;
        }
        catch
        {
            // Not a ZIP -- single file (MSI, EXE, etc.)
            return ContentType.SingleFile;
        }
    }

    /// <summary>Places downloaded content into the workspace based on its type.</summary>
    private static void PlaceContent(string filePath, string bundleRoot, AppConfigModel config, ContentType contentType)
    {
        var framework = Services.ScriptFrameworkProvider.Parse(config.App.ScriptFramework);
        var binaryFolder = framework == ScriptFramework.PSADT ? "Files" : "B";
        var binaryDir = System.IO.Path.Combine(bundleRoot, binaryFolder);

        switch (contentType)
        {
            case ContentType.AppeaseBundle:
                AppLogger.Info("Inventory: extracting Appease bundle into workspace");
                System.IO.Compression.ZipFile.ExtractToDirectory(filePath, bundleRoot, overwriteFiles: true);
                TryDelete(filePath);
                // Update framework to match the downloaded content
                config.App.ScriptFramework = "Appease";
                break;

            case ContentType.PsadtBundle:
                AppLogger.Info("Inventory: extracting PSADT bundle into workspace");
                System.IO.Compression.ZipFile.ExtractToDirectory(filePath, bundleRoot, overwriteFiles: true);
                TryDelete(filePath);
                config.App.ScriptFramework = "PSADT";
                break;

            case ContentType.SingleFile:
            case ContentType.CompressedArchive:
                // Move to the binary folder (B/ or Files/)
                System.IO.Directory.CreateDirectory(binaryDir);
                var fileName = System.IO.Path.GetFileName(filePath);
                var destPath = System.IO.Path.Combine(binaryDir, fileName);
                if (System.IO.File.Exists(destPath)) TryDelete(destPath);
                System.IO.File.Move(filePath, destPath);
                AppLogger.Info($"Inventory: placed '{fileName}' in {binaryFolder}/");

                var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                if (ext == ".msi")
                    config.App.MSIFile = fileName;
                else
                    config.App.EXEFile = fileName;
                break;
        }
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch { /* best effort */ }
    }

    private static void MapIntunePackage(AppConfigModel config, AppInventoryDetail detail)
    {
        var pkg = new IntunePackageEntry
        {
            AppName = detail.DisplayName,
            AppVersion = detail.Version,
            Comment = detail.Description,
            InstallCommand = detail.InstallCommand,
            UninstallCommand = detail.UninstallCommand,
            InstallExperience = detail.InstallExperience,
            RestartBehavior = detail.RestartBehavior,
            MaximumInstallationTimeInMinutes = detail.MaxInstallTime > 0 ? detail.MaxInstallTime : 60,
            CompanyPortalFeaturedApp = detail.IsFeatured,
            Developer = detail.Developer,
            Owner = detail.Owner,
            InformationURL = detail.InformationUrl,
            PrivacyURL = detail.PrivacyUrl,
            Architecture = detail.Architecture,
            MinimumSupportedWindowsRelease = detail.MinimumOSVersion,
        };

        // Categories
        foreach (var cat in detail.Categories)
            pkg.Categories.Add(new TagEntry { Name = cat });

        // Scope tags
        foreach (var tag in detail.ScopeTags)
            pkg.ScopeTags.Add(new TagEntry { Name = tag });

        // Return codes
        foreach (var rc in detail.ReturnCodes)
            pkg.CustomReturnCodes.Add(new ReturnCodeEntry { ReturnCode = rc.Code, Type = rc.Type });

        // Dependencies
        foreach (var dep in detail.Dependencies)
            pkg.Dependencies.Add(new DependencyEntry { AppName = dep.AppName, AutoInstall = dep.AutoInstall });

        // Supersedence
        // Graph API returns "update" or "replace" (lowercase).
        // "Replace" = uninstall previous version (Yes), "Update" = do not uninstall (No).
        foreach (var sup in detail.Supersedence)
        {
            var supType = string.Equals(sup.Type, "update", StringComparison.OrdinalIgnoreCase)
                ? "Update" : "Replace";
            pkg.Supersedence.Add(new SupersedenceEntry { AppName = sup.AppName, SupersedenceType = supType });
        }

        // Assignments
        foreach (var asn in detail.Assignments)
        {
            // Map OData target type to config-friendly values
            var type = asn.TargetType switch
            {
                var t when t.Contains("allDevices", StringComparison.OrdinalIgnoreCase) => "AllDevices",
                var t when t.Contains("allLicensedUsers", StringComparison.OrdinalIgnoreCase) => "AllUsers",
                var t when t.Contains("exclusionGroup", StringComparison.OrdinalIgnoreCase) => "Group",
                var t when t.Contains("group", StringComparison.OrdinalIgnoreCase) => "Group",
                _ => "Group"
            };
            var entry = new AssignmentEntry
            {
                AppName = detail.DisplayName,
                PackageId = pkg.PackageId,
                Type = type,
                GroupID = asn.GroupId,
                GroupMode = asn.GroupMode.ToLowerInvariant(),
                Intent = asn.Intent.ToLowerInvariant(),
                Notification = asn.Notification,
                AvailableTime = asn.AvailableTime,
                DeadlineTime = asn.DeadlineTime,
                DeliveryOptimizationPriority = asn.DeliveryOptimization,
                FilterName = asn.FilterId,
                FilterMode = asn.FilterMode,
            };
            // Set label from resolved group name if available
            if (!string.IsNullOrEmpty(asn.TargetLabel)
                && asn.TargetLabel != "All Devices"
                && asn.TargetLabel != "All Users")
                entry.Label = asn.TargetLabel;
            pkg.Assignments.Add(entry);
        }

        config.Script.IntunePackager.Packages.Add(pkg);
    }

    private static void MapSccmPackage(AppConfigModel config, AppInventoryDetail detail)
    {
        var pkg = new SCCMPackageEntry
        {
            AppName = detail.DisplayName,
            Publisher = detail.Publisher,
            SoftwareVersion = detail.Version,
            Description = detail.Description,
            LocalizedName = detail.DisplayName,
            InstallCommand = detail.InstallCommand,
            UninstallCommand = detail.UninstallCommand,
            InstallationBehaviorType = detail.InstallationBehaviorType,
            Name = detail.DeploymentTypeName,
        };

        // Assignments -> Deployments
        foreach (var asn in detail.Assignments)
        {
            pkg.Deployments.Add(new SCCMDeploymentEntry
            {
                AppName = detail.DisplayName,
                PackageId = pkg.PackageId,
                Collection = asn.TargetLabel,
                DeployPurpose = asn.Intent,
                UserNotification = asn.Notification,
            });
        }

        // Dependencies
        foreach (var dep in detail.Dependencies)
            pkg.Dependencies.Add(new DependencyEntry { AppName = dep.AppName, AutoInstall = dep.AutoInstall });

        config.Script.SCCMPackager.Packages.Add(pkg);
    }
}
