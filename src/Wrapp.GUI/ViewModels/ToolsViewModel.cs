using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class ToolsViewModel : ObservableObject
{
    private readonly EncryptionKeyStoreService _keyStore;
    private readonly IntuneWinDecryptOrchestrator _decryptOrchestrator;
    private readonly AppSettings _settings;
    private readonly IFeatureGate _featureGate;
    private BackgroundJobTracker? _jobTracker;

    public void WireJobTracker(BackgroundJobTracker tracker) => _jobTracker = tracker;

    // -----------------------------------------------------------------------
    // Inspect
    // -----------------------------------------------------------------------

    [ObservableProperty] private string _inspectFilePath = "";
    [ObservableProperty] private EncryptionKeyInfo? _inspectResult;
    [ObservableProperty] private string _inspectStatus = "";
    [ObservableProperty] private bool _hasInspectResult;
    [ObservableProperty] private string _vaultFileName = "";
    [ObservableProperty] private bool _isInspecting;

    // -----------------------------------------------------------------------
    // Decrypt
    // -----------------------------------------------------------------------

    [ObservableProperty] private string _decryptFilePath = "";
    [ObservableProperty] private string _decryptKeySource = "embedded"; // embedded, manual, vault, bruteforce, csv
    [ObservableProperty] private string _manualKey = "";
    [ObservableProperty] private string _manualIV = "";
    [ObservableProperty] private string _vaultAppId = "";
    [ObservableProperty] private string _vaultTenantId = "";
    [ObservableProperty] private string _csvFilePath = "";
    [ObservableProperty] private string _decryptStatus = "";
    [ObservableProperty] private int _decryptProgress;
    [ObservableProperty] private bool _isDecrypting;
    [ObservableProperty] private bool _embeddedKeysReadOnly;

    public bool CanDecrypt =>
        !IsDecrypting
        && !string.IsNullOrEmpty(DecryptFilePath)
        && DecryptKeySource switch
        {
            "embedded" or "manual" => !string.IsNullOrEmpty(ManualKey) && !string.IsNullOrEmpty(ManualIV),
            "vault" => !string.IsNullOrEmpty(VaultTenantId) && !string.IsNullOrEmpty(VaultAppId),
            "bruteforce" => true,
            "csv" => !string.IsNullOrEmpty(CsvFilePath),
            _ => false
        };

    public ToolsViewModel(
        EncryptionKeyStoreService keyStore,
        IntuneWinDecryptOrchestrator decryptOrchestrator,
        AppSettings settings,
        IFeatureGate featureGate)
    {
        _keyStore = keyStore;
        _decryptOrchestrator = decryptOrchestrator;
        _settings = settings;
        _featureGate = featureGate;

        // CanSaveBatch reads the gate; rebroadcast whenever the gate flips so
        // the "Save Batch to Vault" button greys/un-greys without a restart.
        _featureGate.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanSaveBatch));
    }

    // Notify CanDecrypt whenever any input changes
    partial void OnDecryptFilePathChanged(string value) => OnPropertyChanged(nameof(CanDecrypt));
    partial void OnDecryptKeySourceChanged(string value) => OnPropertyChanged(nameof(CanDecrypt));
    partial void OnManualKeyChanged(string value) => OnPropertyChanged(nameof(CanDecrypt));
    partial void OnManualIVChanged(string value) => OnPropertyChanged(nameof(CanDecrypt));
    partial void OnVaultAppIdChanged(string value) => OnPropertyChanged(nameof(CanDecrypt));
    partial void OnVaultTenantIdChanged(string value) => OnPropertyChanged(nameof(CanDecrypt));
    partial void OnCsvFilePathChanged(string value) => OnPropertyChanged(nameof(CanDecrypt));
    partial void OnIsDecryptingChanged(bool value) => OnPropertyChanged(nameof(CanDecrypt));

    // -----------------------------------------------------------------------
    // Inspect commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task BrowseInspectFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "IntuneWin files (*.intunewin)|*.intunewin|All files (*.*)|*.*",
            Title = "Select .intunewin File"
        };
        if (dialog.ShowDialog() == true)
        {
            InspectFilePath = dialog.FileName;
            await InspectPackage();
        }
    }

    [RelayCommand]
    private async Task InspectPackage()
    {
        if (string.IsNullOrEmpty(InspectFilePath) || !File.Exists(InspectFilePath))
        {
            InspectStatus = "File not found.";
            InspectResult = null;
            HasInspectResult = false;
            return;
        }

        IsInspecting = true;
        InspectStatus = "Reading detection.xml...";
        AppLogger.Info($"IntuneWin inspect: starting -- {InspectFilePath}");

        // Off-thread: the ZIP open can block for seconds on Mark-of-the-Web
        // files (Defender scans on first open) or network shares.
        InspectResult = await Task.Run(() => IntuneWinService.InspectPackage(InspectFilePath));
        HasInspectResult = InspectResult is not null;

        if (HasInspectResult)
        {
            AppLogger.Info($"IntuneWin inspect: metadata found -- pkg={InspectResult!.PackageName}, setup={InspectResult.SetupFile}, hasKey={!string.IsNullOrEmpty(InspectResult.EncryptionKey)}");

            // Auto-populate vault filename from Config.json identity
            InspectStatus = "Decrypting inner package to read Config.json...";
            var identity = await IntuneWinService.ExtractAppIdentityAsync(InspectFilePath);
            VaultFileName = identity is not null
                ? $"{identity.Value.Name}_{identity.Value.Version}"
                : InspectResult.PackageName;
            AppLogger.Info($"IntuneWin inspect: identity resolved -- vaultName={VaultFileName}");
            InspectStatus = $"Package: {InspectResult.PackageName} | Setup: {InspectResult.SetupFile} | Size: {InspectResult.UnencryptedContentSize:N0} bytes";
        }
        else
        {
            InspectStatus = "Not a valid .intunewin package (no detection.xml found).";
            AppLogger.Warn("IntuneWin inspect: no detection.xml found");
            VaultFileName = "";
        }

        IsInspecting = false;
    }

    [RelayCommand]
    private void CopyInspectKeys()
    {
        if (InspectResult is null) return;
        var json = JsonSerializer.Serialize(InspectResult, JsonDefaults.Pretty);
        System.Windows.Clipboard.SetText(json);
        InspectStatus = "Keys copied to clipboard.";
    }

    [RelayCommand]
    private async Task SaveInspectKeysToVault()
    {
        if (InspectResult is null) return;
        if (string.IsNullOrEmpty(InspectResult.EncryptionKey))
        {
            InspectStatus = "No encryption key to save.";
            return;
        }

        // Phase 16b: silent-skip when the vault gate is OFF. Surfaces a
        // friendly status line + log entry so the no-op is diagnosable,
        // but avoids the "configure your vault" dialog that fires when
        // the URL is unset -- the user opted out, no need to nag.
        if (!_featureGate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault))
        {
            InspectStatus = "Vault disabled in Settings -- keys not saved.";
            AppLogger.Info("Tools.SaveInspectKeysToVault: vault gate OFF, skipping. "
                + _featureGate.DescribeWhyDisabled(WrappFeatures.AzureDevOpsKeyVault));
            return;
        }

        var packageName = VaultFileName.Trim();
        if (string.IsNullOrEmpty(packageName)) packageName = InspectResult.PackageName;

        // Load DevOps vault keys and check for collisions (same logic as batch mode)
        InspectStatus = "Checking vault for duplicates...";
        _cachedDevOpsKeys = new();
        if (!string.IsNullOrEmpty(_settings.KeyVaultRepoUrl))
        {
            try { _cachedDevOpsKeys = await _keyStore.LoadAllDevOpsKeysAsync(); }
            catch (Exception ex) { AppLogger.Warn($"DevOps key cache load failed: {ex.Message}"); }
        }
        var (resolvedName, cancelAll) = await ResolveVaultCollisionsAsync(
            InspectResult.EncryptionKey, InspectResult.InitializationVector, packageName);
        if (resolvedName is null)
        {
            InspectStatus = "Save skipped (key already exists or cancelled).";
            return;
        }
        packageName = resolvedName;

        var keys = new EncryptionKeyInfo
        {
            AppId                  = InspectResult.AppId,
            DisplayName            = VaultFileName.Trim(),
            TenantId               = InspectResult.TenantId,
            EncryptionKey          = InspectResult.EncryptionKey,
            InitializationVector   = InspectResult.InitializationVector,
            MacKey                 = InspectResult.MacKey,
            Mac                    = InspectResult.Mac,
            ProfileIdentifier      = InspectResult.ProfileIdentifier,
            FileDigest             = InspectResult.FileDigest,
            FileDigestAlgorithm    = InspectResult.FileDigestAlgorithm,
            PackageName            = packageName,
            SetupFile              = InspectResult.SetupFile,
            InnerFileName          = InspectResult.InnerFileName,
            UnencryptedContentSize = InspectResult.UnencryptedContentSize,
            SourcePath             = InspectResult.SourcePath,
            SavedAt                = SystemClock.UtcNow.ToString("o"),
            SavedBy                = Environment.UserName,
        };

        try
        {
            await _keyStore.SaveKeysAsync(keys);
            InspectStatus = $"Keys saved to vault as '{packageName}.json'.";
        }
        catch (EncryptionKeyStoreService.DevOpsVaultNotConfiguredException ex)
        {
            InspectStatus = "Keys not saved -- DevOps vault is not configured (Settings -> Key Vault).";
            AppLogger.Warn($"Tools.SaveKeys: DevOps vault not configured -- '{packageName}' discarded. {ex.Message}");
            await FluentDialog.ShowInfoAsync(
                "DevOps vault not configured",
                "Encryption keys can only be saved to the Azure DevOps vault. Configure it in Settings -> Key Vault, then re-extract the keys.");
        }
        catch (Exception ex)
        {
            InspectStatus = $"Save failed: {ex.Message}";
            AppLogger.Warn($"Tools.SaveKeys: failed to save keys for '{packageName}' -- {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Decrypt commands
    // -----------------------------------------------------------------------

    /// <summary>Called when a file is dropped on the Decrypt tab. Auto-detects embedded keys.</summary>
    public async Task HandleDecryptDropAsync(string path)
    {
        DecryptFilePath = path;
        await DetectEmbeddedKeysAsync(path);
    }

    /// <summary>
    /// Inspects <paramref name="path"/> for embedded encryption keys (from the
    /// .intunewin's detection.xml) and, when present, selects the embedded key
    /// source and fills the key/IV fields. Shared by the drag-drop
    /// (<see cref="HandleDecryptDrop"/>) and Browse (<see cref="BrowseDecryptFileCommand"/>)
    /// entry points so both honour the "keys load automatically" behaviour the
    /// Decrypt tab's help text promises. Browse previously skipped this, leaving
    /// the embedded fields empty and the Decrypt button greyed.
    /// </summary>
    private async Task DetectEmbeddedKeysAsync(string path)
    {
        AppLogger.Info($"IntuneWin decrypt: file loaded -- {path}");
        DecryptStatus = "Reading embedded keys...";
        var metadata = await Task.Run(() => IntuneWinService.InspectPackage(path));
        if (metadata is not null && !string.IsNullOrEmpty(metadata.EncryptionKey))
        {
            DecryptKeySource = "embedded";
            ManualKey = metadata.EncryptionKey;
            ManualIV = metadata.InitializationVector;
            EmbeddedKeysReadOnly = true;
            DecryptStatus = $"Embedded keys detected and loaded. Setup: {metadata.SetupFile} ({metadata.UnencryptedContentSize:N0} bytes)";
            AppLogger.Info($"IntuneWin decrypt: embedded keys found -- setup={metadata.SetupFile}, size={metadata.UnencryptedContentSize}");
        }
        else
        {
            DecryptStatus = "No embedded keys detected. Select a key source and click Decrypt.";
            AppLogger.Info("IntuneWin decrypt: no embedded keys in file");
        }
    }

    [RelayCommand]
    private async Task BrowseDecryptFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "IntuneWin files (*.intunewin)|*.intunewin|All files (*.*)|*.*",
            Title = "Select File to Decrypt"
        };
        if (dialog.ShowDialog() == true)
        {
            DecryptFilePath = dialog.FileName;
            // Match the drag-drop path: auto-detect embedded keys on browse too.
            await DetectEmbeddedKeysAsync(dialog.FileName);
        }
    }

    [RelayCommand]
    private void BrowseCsvFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            Title = "Select CSV Key File"
        };
        if (dialog.ShowDialog() == true)
            CsvFilePath = dialog.FileName;
    }

    private string? PickOutputFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select output folder for decrypted files",
            UseDescriptionForTitle = true
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    [RelayCommand]
    private async Task DecryptFileAsync()
    {
        if (string.IsNullOrEmpty(DecryptFilePath) || !File.Exists(DecryptFilePath))
        {
            DecryptStatus = "Select a file first.";
            return;
        }

        // Pick output folder up front (before starting work)
        var outputFolder = PickOutputFolder();
        if (outputFolder is null) return;

        IsDecrypting = true;
        DecryptProgress = 0;
        var progress = UiProgress.ForProgress(p => DecryptProgress = p);

        // Build a step tree that matches the chosen key source. Embedded
        // uses one combined step (extract+decrypt is atomic in the
        // orchestrator); every other source separates blob-prep,
        // key-load, attempt, and validate so the user can see which
        // phase took time or failed.
        var stepTree = new JobStepTree();
        JobStep stepPrep;
        JobStep stepKeys;
        JobStep stepAttempt;
        JobStep stepValidate = stepTree.Add("Validate output");
        if (DecryptKeySource == "embedded")
        {
            stepPrep    = stepTree.Add("Extract + decrypt with embedded keys");
            stepKeys    = stepPrep;    // collapsed into the extract+decrypt step
            stepAttempt = stepPrep;
            // Re-order: move validate to the end of the list. (Already at end.)
        }
        else
        {
            stepTree.Steps.Clear();
            stepPrep    = stepTree.Add("Prepare encrypted blob");
            stepKeys    = stepTree.Add(DecryptKeySource switch
            {
                "manual"     => "Use manual key/IV",
                "vault"      => $"Load vault keys for {VaultTenantId}/{VaultAppId}",
                "bruteforce" => "Load every key in the vault",
                "csv"        => "Parse CSV key list",
                _            => "Prepare keys",
            });
            stepAttempt = stepTree.Add("Attempt decrypt");
            stepValidate = stepTree.Add("Validate output");
        }

        var decryptJob = _jobTracker?.BeginJob(
            $"Decrypting {Path.GetFileName(DecryptFilePath)}",
            bundleRoot: null, context: stepTree) ?? default;

        // For non-embedded modes, extract the inner encrypted blob from the .intunewin ZIP
        // (if it is a ZIP). Embedded mode handles this internally via ExtractAndDecryptAsync.
        string? extractedBlob = null;

        using var op = OperationScope.Begin($"Tools.Decrypt({DecryptKeySource})");
        try
        {
            AppLogger.Info($"IntuneWin decrypt: source={DecryptKeySource}, file={DecryptFilePath}, output={outputFolder}");
            var statusProgress = UiProgress.ForStatus(msg =>
            {
                DecryptStatus = msg;
                // Surface live progress (e.g., "Trying key 3 of 42") on the
                // attempt step so the popup tree reflects the current state.
                if (stepAttempt.State == StepState.Running)
                    stepAttempt.StatusMessage = msg;
                else if (stepKeys.State == StepState.Running)
                    stepKeys.StatusMessage = msg;
            });
            var baseName = Path.GetFileNameWithoutExtension(DecryptFilePath);
            IntuneWinDecryptOrchestrator.DecryptResult result;

            if (DecryptKeySource == "embedded")
            {
                stepPrep.Start("Reading Detection.xml + decrypting in one pass");
                DecryptStatus = "Extracting and decrypting with embedded keys...";
                result = await _decryptOrchestrator.DecryptWithEmbeddedKeysAsync(DecryptFilePath, outputFolder, progress);
                DecryptStatus = result.Message;
                stepPrep.Finish(
                    result.Success ? StepState.Succeeded : StepState.Failed,
                    result.Message);
                stepValidate.Finish(
                    result.Success ? StepState.Succeeded : StepState.Skipped,
                    result.Success ? "File signature recognised" : "");
            }
            else
            {
                // ── Prepare blob ───────────────────────────────────
                stepPrep.Start();
                DecryptStatus = "Preparing encrypted blob...";
                extractedBlob = await Task.Run(() => IntuneWinDecryptOrchestrator.PrepareBlob(DecryptFilePath));
                if (extractedBlob is null)
                {
                    DecryptStatus = "Could not read the encrypted file.";
                    stepPrep.Finish(StepState.Failed, "Could not read");
                    stepKeys.Finish(StepState.Skipped);
                    stepAttempt.Finish(StepState.Skipped);
                    stepValidate.Finish(StepState.Skipped);
                    return;
                }
                stepPrep.Finish(StepState.Succeeded);
                var blobPath = extractedBlob;

                // ── Key source + attempt ───────────────────────────
                stepKeys.Start();
                stepAttempt.Start();
                if (DecryptKeySource == "manual")
                {
                    result = await _decryptOrchestrator.DecryptWithKeyPairAsync(
                        blobPath, outputFolder, ManualKey.Trim(), ManualIV.Trim(), progress, baseName);
                }
                else if (DecryptKeySource == "vault")
                {
                    result = await _decryptOrchestrator.DecryptWithVaultAsync(
                        blobPath, outputFolder, VaultTenantId.Trim(), VaultAppId.Trim(), progress, statusProgress, baseName);
                    if (result.WinningKey is not null)
                    {
                        ManualKey = result.WinningKey;
                        ManualIV = result.WinningIV!;
                    }
                }
                else if (DecryptKeySource == "bruteforce")
                {
                    result = await _decryptOrchestrator.BruteForceDecryptAsync(
                        blobPath, outputFolder, progress, statusProgress, baseName);
                    if (result.WinningKey is not null)
                    {
                        ManualKey = result.WinningKey;
                        ManualIV = result.WinningIV!;
                        EmbeddedKeysReadOnly = false;
                    }
                }
                else if (DecryptKeySource == "csv")
                {
                    result = await _decryptOrchestrator.CsvDecryptAsync(
                        blobPath, outputFolder, CsvFilePath, progress, statusProgress, baseName);
                    if (result.WinningKey is not null)
                    {
                        ManualKey = result.WinningKey;
                        ManualIV = result.WinningIV!;
                        EmbeddedKeysReadOnly = false;
                    }
                }
                else
                {
                    result = new IntuneWinDecryptOrchestrator.DecryptResult(false, null, "Select a key source.");
                }

                DecryptStatus = result.Message;
                // Stamp the two source-specific steps with the outcome.
                stepKeys.Finish(
                    result.Success ? StepState.Succeeded : StepState.Failed,
                    result.WinningKey is not null ? "Matched a key" : "");
                stepAttempt.Finish(
                    result.Success ? StepState.Succeeded : StepState.Failed,
                    result.Message);
                stepValidate.Finish(
                    result.Success ? StepState.Succeeded : StepState.Skipped,
                    result.Success ? "File signature recognised" : "");
            }

            op.Complete($"source={DecryptKeySource}, file={Path.GetFileName(DecryptFilePath)}");
        }
        catch (Exception ex)
        {
            DecryptStatus = $"Error: {ex.Message}";
            op.Fail(ex, $"source={DecryptKeySource}");
            // Mark any still-Running step as Failed so the tree reflects it.
            foreach (var step in stepTree.Steps)
            {
                if (step.State == StepState.Running) step.Finish(StepState.Failed, ex.Message);
                if (step.State == StepState.Pending) step.Finish(StepState.Skipped);
            }
            decryptJob.Fail(ex.Message);
        }
        finally
        {
            if (extractedBlob is not null && extractedBlob != DecryptFilePath)
            {
                try { File.Delete(extractedBlob); }
                catch (Exception ex) { AppLogger.Warn($"Tools.Decrypt: temp blob cleanup failed for {extractedBlob} -- {ex.Message}"); }
            }
            IsDecrypting = false;
            if (decryptJob.IsActive && decryptJob.Job is { IsCompleted: false })
                decryptJob.Complete(DecryptStatus);
        }
    }

    // -----------------------------------------------------------------------
    // Batch Inspect
    // -----------------------------------------------------------------------

    [ObservableProperty] private string _batchFolderPath = "";
    [ObservableProperty] private bool _batchRecursive;
    [ObservableProperty] private bool _isBatchScanning;
    [ObservableProperty] private bool _isBatchSaving;
    [ObservableProperty] private string _batchStatus = "";
    [ObservableProperty] private int _batchProgress;

    public ObservableCollection<BatchInspectResult> BatchResults { get; } = new();

    public bool CanSaveBatch => !IsBatchScanning && !IsBatchSaving
        && BatchResults.Any(r => r.HasKeys)
        && _featureGate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault);

    partial void OnIsBatchScanningChanged(bool value) => OnPropertyChanged(nameof(CanSaveBatch));
    partial void OnIsBatchSavingChanged(bool value) => OnPropertyChanged(nameof(CanSaveBatch));

    [RelayCommand]
    private void BrowseBatchFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder containing .intunewin files",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            BatchFolderPath = dialog.SelectedPath;
    }

    private CancellationTokenSource? _batchCts;
    // Cached during scan for reuse in save and collision checks. DevOps is the
    // sole source of truth -- the per-workstation local cache was removed in
    // Phase 10 (see 0.6.0.0220).
    private List<(EncryptionKeyInfo Key, string RelativePath)> _cachedDevOpsKeys = new();

    [RelayCommand]
    private async Task ScanBatchAsync()
    {
        if (string.IsNullOrEmpty(BatchFolderPath) || !Directory.Exists(BatchFolderPath))
        {
            BatchStatus = "Select a valid folder first.";
            return;
        }

        using var op = OperationScope.Begin("Tools.BatchInspect");
        IsBatchScanning = true;
        BatchResults.Clear();
        OnPropertyChanged(nameof(CanSaveBatch));
        BatchProgress = 0;
        // Dispose any prior CTS before reassigning so linked tokens don't pile up.
        _batchCts?.Dispose();
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        // Enumerate files off the UI thread (recursive search on large trees can be slow)
        var searchOption = BatchRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        BatchStatus = BatchRecursive
            ? "Searching folders recursively for .intunewin files..."
            : "Searching for .intunewin files...";
        var scopeSuffix = BatchRecursive ? " (recursive)" : "";
        var job = _jobTracker?.BeginJob($"Batch inspect: {BatchFolderPath}{scopeSuffix}") ?? default;
        job.SetStatus(BatchStatus);

        string[] files;
        try
        {
            files = await Task.Run(() => Directory.GetFiles(BatchFolderPath, "*.intunewin", searchOption), ct);
        }
        catch (OperationCanceledException)
        {
            BatchStatus = "Search cancelled.";
            job.Complete("Cancelled");
            IsBatchScanning = false;
            _batchCts?.Dispose();
            _batchCts = null;
            OnPropertyChanged(nameof(CanSaveBatch));
            return;
        }

        if (files.Length == 0)
        {
            BatchStatus = "No .intunewin files found.";
            job.Complete("No files found");
            IsBatchScanning = false;
            _batchCts?.Dispose();
            _batchCts = null;
            OnPropertyChanged(nameof(CanSaveBatch));
            return;
        }

        // Update job title now that we know the count
        job.SetStatus($"Batch inspect: {files.Length} files");
        AppLogger.Info($"Batch inspect: found {files.Length} file(s) in {BatchFolderPath} (recursive={BatchRecursive})");

        // Load DevOps vault keys for dedup (cached for save phase too).
        _cachedDevOpsKeys = new();
        if (!string.IsNullOrEmpty(_settings.KeyVaultRepoUrl))
        {
            BatchStatus = "Loading DevOps vault keys...";
            job.SetStatus(BatchStatus);
            try { _cachedDevOpsKeys = await _keyStore.LoadAllDevOpsKeysAsync(); }
            catch (Exception ex) { AppLogger.Warn($"Batch inspect: DevOps load failed -- {ex.Message}"); }
        }
        var devOpsKeys = _cachedDevOpsKeys;

        int skippedExisting = 0;

        try
        {
            for (int i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var file = files[i];
                var fileName = Path.GetFileName(file);
                BatchStatus = $"Processing {i + 1}/{files.Length}: {fileName}...";
                job.SetStatus(BatchStatus);
                BatchProgress = (int)((i + 1) * 100.0 / files.Length);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await Task.Run(async () =>
                {
                    var metadata = IntuneWinService.InspectPackage(file);
                    if (metadata is null || string.IsNullOrEmpty(metadata.EncryptionKey))
                    {
                        return new BatchInspectResult
                        {
                            FileName = fileName, FilePath = file,
                            HasKeys = false, StatusLabel = "No metadata",
                            AppName = "", Version = "", VaultName = "",
                            KeyPreview = "", SizeDisplay = "", KeyInfo = null
                        };
                    }

                    // Check key/IV pair against the DevOps vault (single source of truth).
                    var devOpsMatch = devOpsKeys
                        .FirstOrDefault(k => k.Key.EncryptionKey == metadata.EncryptionKey
                                          && k.Key.InitializationVector == metadata.InitializationVector);
                    if (devOpsMatch.Key is not null)
                    {
                        var matchEntry = devOpsMatch.Key;
                        var existName = !string.IsNullOrEmpty(matchEntry.DisplayName)
                            ? matchEntry.DisplayName : matchEntry.PackageName;
                        AppLogger.Info($"Batch inspect: Exists -- {fileName} matches '{existName}' at {devOpsMatch.RelativePath}");
                        return new BatchInspectResult
                        {
                            FileName = fileName, FilePath = file,
                            HasKeys = true, StatusLabel = "Exists",
                            ExistsVault = true,
                            AppName = existName, Version = "",
                            VaultName = matchEntry.PackageName,
                            KeyPreview = metadata.EncryptionKey,
                            SizeDisplay = BatchInspectResult.FormatSize(metadata.UnencryptedContentSize),
                            KeyInfo = metadata
                        };
                    }

                    // Check cancellation before the expensive decrypt+extract
                    ct.ThrowIfCancellationRequested();

                    // Resolve identity from Config.json inside the package
                    var identity = await IntuneWinService.ExtractAppIdentityAsync(file);
                    var appName = identity?.Name ?? metadata.PackageName;
                    var version = identity?.Version ?? "0_0_0";
                    var vaultName = $"{appName}_{version}";
                    return new BatchInspectResult
                    {
                        FileName = fileName, FilePath = file,
                        HasKeys = true, StatusLabel = "OK",
                        AppName = appName, Version = version,
                        VaultName = vaultName, KeyPreview = metadata.EncryptionKey,
                        SizeDisplay = BatchInspectResult.FormatSize(metadata.UnencryptedContentSize),
                        KeyInfo = metadata
                    };
                }, ct);
                sw.Stop();
                result.ElapsedDisplay = sw.Elapsed.TotalMinutes >= 1
                    ? $"{(int)sw.Elapsed.TotalMinutes}m {sw.Elapsed.Seconds}s"
                    : $"{sw.Elapsed.TotalSeconds:F1}s";
                AppLogger.Info($"Batch inspect: {result.StatusLabel} -- {fileName} ({result.ElapsedDisplay}){(result.HasKeys ? $" -> {result.VaultName}" : "")}");

                if (result.StatusLabel == "Exists") skippedExisting++;
                BatchResults.Add(result);
            }
        }
        catch (OperationCanceledException)
        {
            BatchStatus = $"Scan cancelled after {BatchResults.Count}/{files.Length} files.";
            AppLogger.Info($"Batch inspect: cancelled after {BatchResults.Count} files");
            job.Complete("Cancelled");
            IsBatchScanning = false;
            _batchCts?.Dispose();
            _batchCts = null;
            OnPropertyChanged(nameof(CanSaveBatch));
            return;
        }

        var okCount = BatchResults.Count(r => r.HasKeys && r.StatusLabel == "OK");
        var existsCount = BatchResults.Count(r => r.StatusLabel == "Exists");
        var noMetaCount = BatchResults.Count(r => !r.HasKeys);
        var totalTime = job.Job?.ElapsedDisplay ?? "";
        var existsText = existsCount > 0 ? $", {existsCount} already in vault" : "";
        BatchStatus = $"Scan complete: {okCount} new{existsText}, {noMetaCount} no metadata. Total: {totalTime}";
        op.Complete($"{okCount} new, {existsCount} in vault, {noMetaCount} no metadata");
        job.Complete(BatchStatus);
        IsBatchScanning = false;
        _batchCts = null;
        OnPropertyChanged(nameof(CanSaveBatch));
    }

    [RelayCommand]
    private void CancelBatch()
    {
        _batchCts?.Cancel();
        AppLogger.Info("Batch inspect: cancel requested");
    }

    /// <summary>Builds an EncryptionKeyInfo from a batch result with the given package name.</summary>
    private static EncryptionKeyInfo BuildKeyInfo(BatchInspectResult item, string packageName) => new()
    {
        AppId                  = item.KeyInfo!.AppId,
        DisplayName            = item.AppName,
        TenantId               = item.KeyInfo.TenantId,
        EncryptionKey          = item.KeyInfo.EncryptionKey,
        InitializationVector   = item.KeyInfo.InitializationVector,
        MacKey                 = item.KeyInfo.MacKey,
        Mac                    = item.KeyInfo.Mac,
        ProfileIdentifier      = item.KeyInfo.ProfileIdentifier,
        FileDigest             = item.KeyInfo.FileDigest,
        FileDigestAlgorithm    = item.KeyInfo.FileDigestAlgorithm,
        PackageName            = packageName,
        SetupFile              = item.KeyInfo.SetupFile,
        InnerFileName          = item.KeyInfo.InnerFileName,
        UnencryptedContentSize = item.KeyInfo.UnencryptedContentSize,
        SourcePath             = item.KeyInfo.SourcePath,
        SavedAt                = SystemClock.UtcNow.ToString("o"),
        SavedBy                = Environment.UserName,
    };

    /// <summary>
    /// Collision check for saving a keyset to the DevOps vault. Detects
    /// duplicate key/IV pairs and filename collisions. Returns the resolved
    /// name, or null to skip. Used by both batch save and single inspect save.
    /// </summary>
    private async Task<(string? Name, bool CancelAll)> ResolveVaultCollisionsAsync(
        string encryptionKey, string initializationVector, string packageName)
    {
        // Check 1: key/IV pair already in the vault?
        var existingByPair = _cachedDevOpsKeys
            .FirstOrDefault(k => k.Key.EncryptionKey == encryptionKey
                              && k.Key.InitializationVector == initializationVector);
        if (existingByPair.Key is not null)
        {
            var existingPath = existingByPair.RelativePath;
            if (existingPath.EndsWith($"{packageName}.json", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info($"Batch save: '{packageName}.json' exists with same key/IV -- skipping");
                return (null, false); // skip, identical
            }

            AppLogger.Info($"Batch save: key/IV for '{packageName}' already exists at {existingPath}");
            var options = new ActionPickerOption[]
            {
                new() { Key = "save", Icon = "\uE74E", Title = "Save anyway",
                    Description = $"Save '{packageName}.json' as a second copy of this key." },
                new() { Key = "skip", Icon = "\uE711", Title = "Skip",
                    Description = $"Key already exists at {existingPath}. Do not create a duplicate." },
            };
            var dialog = new Views.ActionPickerDialog(
                $"This key/IV pair already exists as '{existingPath}'.", options, defaultKey: "skip");
            var confirmed = await FluentDialog.ShowSelectAsync($"Duplicate key: {packageName}", dialog, "Continue", "Cancel All");
            if (!confirmed) return (null, true);
            return dialog.SelectedKey == "skip" ? (null, false) : (packageName, false);
        }

        // Check 2: filename collision with different key/IV in the DevOps vault.
        var devOpsByName = _cachedDevOpsKeys
            .FirstOrDefault(k => k.RelativePath.EndsWith($"{packageName}.json", StringComparison.OrdinalIgnoreCase));
        if (devOpsByName.Key is null) return (packageName, false); // no collision

        AppLogger.Warn($"Batch save: '{packageName}.json' exists in DevOps vault with DIFFERENT key/IV");
        var opts = new ActionPickerOption[]
        {
            new() { Key = "overwrite", Icon = "\uE74E", Title = "Overwrite",
                Description = $"Replace '{packageName}.json' with this key. NOTE: the previous key stays " +
                    "recoverable in the vault's git history -- treat the old key as compromised and rotate if needed." },
            new() { Key = "copy", Icon = "\uE8C8", Title = "Save as copy",
                Description = $"Save alongside the existing file with a suffix." },
            new() { Key = "skip", Icon = "\uE711", Title = "Skip",
                Description = "Do not save this key." },
        };
        var dlg = new Views.ActionPickerDialog(
            $"'{packageName}.json' exists in the DevOps vault with a DIFFERENT key/IV pair.", opts, defaultKey: "copy");
        var ok = await FluentDialog.ShowSelectAsync($"Filename collision: {packageName}", dlg, "Continue", "Cancel All");
        if (!ok) return (null, true);
        if (dlg.SelectedKey == "skip") return (null, false);
        if (dlg.SelectedKey == "copy")
        {
            int suffix = 2;
            while (_cachedDevOpsKeys.Any(k =>
                k.RelativePath.EndsWith($"{packageName}_{suffix}.json", StringComparison.OrdinalIgnoreCase)))
                suffix++;
            packageName = $"{packageName}_{suffix}";
        }
        return (packageName, false);
    }

    [RelayCommand]
    private async Task SaveBatchToVaultAsync()
    {
        // Phase 16b: silent-skip when the vault gate is OFF. CanSaveBatch
        // already greys the button out, but this guard is the load-bearing
        // check for any code path that calls the command directly (e.g.
        // future automation hooks).
        if (!_featureGate.IsEnabled(WrappFeatures.AzureDevOpsKeyVault))
        {
            BatchStatus = "Vault disabled in Settings -- batch save skipped.";
            AppLogger.Info("Tools.SaveBatchToVaultAsync: vault gate OFF, skipping. "
                + _featureGate.DescribeWhyDisabled(WrappFeatures.AzureDevOpsKeyVault));
            return;
        }

        // OK = new keys (push to DevOps); Exists = already in vault, skip.
        var toSave = BatchResults.Where(r => r.HasKeys && r.KeyInfo is not null
            && r.StatusLabel == "OK").ToList();
        var skippedVault = BatchResults.Count(r => r.StatusLabel == "Exists");
        if (toSave.Count == 0)
        {
            BatchStatus = $"No keys to push.{(skippedVault > 0 ? $" {skippedVault} already in vault." : "")}";
            return;
        }

        IsBatchSaving = true;
        BatchProgress = 0;
        int saved = 0, skipped = 0, failed = 0;
        var job = _jobTracker?.BeginJob($"Pushing {toSave.Count} keys to vault") ?? default;
        AppLogger.Info($"Batch save (vault): {toSave.Count} key(s) to push");

        for (int i = 0; i < toSave.Count; i++)
        {
            var item = toSave[i];
            var packageName = item.VaultName.Trim();
            if (string.IsNullOrEmpty(packageName)) packageName = item.KeyInfo!.PackageName;

            BatchStatus = $"Pushing {i + 1}/{toSave.Count}: {packageName}...";
            job.SetStatus(BatchStatus);
            BatchProgress = (int)((i + 1) * 100.0 / toSave.Count);

            var (resolvedName, cancelAll) = await ResolveVaultCollisionsAsync(item.KeyInfo!.EncryptionKey, item.KeyInfo.InitializationVector, packageName);
            if (cancelAll) break;
            if (resolvedName is null) { skipped++; continue; }

            var keys = BuildKeyInfo(item, resolvedName);
            try
            {
                await _keyStore.SaveKeysAsync(keys);
                saved++;
                AppLogger.Info($"Batch save: pushed '{resolvedName}.json' to vault");
            }
            catch (EncryptionKeyStoreService.DevOpsVaultNotConfiguredException ex)
            {
                AppLogger.Warn($"Batch save: DevOps vault not configured -- aborting remaining pushes. {ex.Message}");
                await FluentDialog.ShowInfoAsync(
                    "DevOps vault not configured",
                    "Encryption keys can only be saved to the Azure DevOps vault. Configure it in Settings -> Key Vault, then re-run the batch save.");
                break;
            }
            catch (Exception ex)
            {
                failed++;
                AppLogger.Warn($"Batch save: failed '{resolvedName}': {ex.Message}");
            }
        }

        var parts = new List<string> { $"Pushed {saved} key(s) to vault." };
        if (failed > 0) parts.Add($"{failed} failed.");
        if (skipped > 0) parts.Add($"{skipped} skipped.");
        if (skippedVault > 0) parts.Add($"{skippedVault} already in vault.");
        BatchStatus = string.Join(" ", parts);
        AppLogger.Info($"Batch save (vault): {saved} saved, {failed} failed, {skipped} skipped, {skippedVault} already in vault");
        job.Complete(BatchStatus);
        IsBatchSaving = false;
    }
}

/// <summary>Single row in the batch inspect results table.</summary>
public partial class BatchInspectResult : ObservableObject
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public bool HasKeys { get; init; }
    public string StatusLabel { get; init; } = "";  // "OK", "No metadata", "Exists"
    public bool ExistsVault { get; init; }
    public string AppName { get; init; } = "";
    public string Version { get; init; } = "";
    [ObservableProperty] private string _vaultName = "";
    public string KeyPreview { get; init; } = "";
    public string SizeDisplay { get; init; } = "";
    public string ElapsedDisplay { get; set; } = "";
    public EncryptionKeyInfo? KeyInfo { get; init; }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
