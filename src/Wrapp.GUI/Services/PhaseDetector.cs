using System.Text.RegularExpressions;

namespace Wrapp.Services;

/// <summary>
/// Parses PowerShell log output to detect orchestrator phase transitions.
/// Fires events that RunViewModel uses to update PackageProgress items.
/// Thread-safe: called from PSDataStreams DataAdded handlers (threadpool).
/// </summary>
public sealed class PhaseDetector
{
    public event Action<PhaseEvent>? PhaseChanged;

    // -- Auth --
    private static readonly Regex RxAuthenticated =
        new(@"Successfully authenticated to MS Graph", RegexOptions.Compiled);

    // -- Preflight --
    private static readonly Regex RxPreflightStarted =
        new(@"Running (?:post-auth|SCCM) preflight", RegexOptions.Compiled);
    private static readonly Regex RxPreflightPassed =
        new(@"(?:Post-auth|SCCM) preflight passed", RegexOptions.Compiled);

    // -- Collision --
    private static readonly Regex RxCollisionStarted =
        new(@"Checking for app name collision", RegexOptions.Compiled);
    private static readonly Regex RxCollisionDetected =
        new(@"Collision: '([^']+)' already exists in Intune", RegexOptions.Compiled);
    private static readonly Regex RxCollisionPassed =
        new(@"Proceeding with (\d+) package", RegexOptions.Compiled);

    // -- Wrapping (.intunewin) --
    private static readonly Regex RxWrappingStarted =
        new(@"Creating package '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex RxWrappingCopying =
        new(@"Copying source files to staging area", RegexOptions.Compiled);
    private static readonly Regex RxWrappingCopied =
        new(@"Source files copied to staging area", RegexOptions.Compiled);
    private static readonly Regex RxWrappingEncoding =
        new(@"Running IntuneWinAppUtil to create \.intunewin package", RegexOptions.Compiled);
    private static readonly Regex RxWrappingCompleted =
        new(@"Package created: (.+)", RegexOptions.Compiled);

    // -- Per-package app processing (Intune) --
    private static readonly Regex RxAppProcessing =
        new(@"Processing package: (.+)", RegexOptions.Compiled);
    private static readonly Regex RxAppCreating =
        new(@"Creating Intune app: (.+)", RegexOptions.Compiled);
    private static readonly Regex RxAppCreated =
        new(@"App created successfully: (.+?) \(ID:", RegexOptions.Compiled);

    // -- Per-package app processing (SCCM) --
    private static readonly Regex RxSccmAppCreating =
        new(@"Creating SCCM application: (.+)", RegexOptions.Compiled);

    // -- Dependencies and supersedence --
    private static readonly Regex RxDependencies =
        new(@"Processing dependencies for '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex RxSupersedence =
        new(@"Processing supersedence for '([^']+)'", RegexOptions.Compiled);

    // -- Assignments --
    private static readonly Regex RxAssignmentWaiting =
        new(@"Waiting for '([^']+)' to become available", RegexOptions.Compiled);
    private static readonly Regex RxAssignmentReady =
        new(@"GUID match confirmed for '([^']+)'", RegexOptions.Compiled);
    private static readonly Regex RxAssignmentResult =
        new(@"Assignment result for '([^']+)': (\d+) applied, (\d+) failed", RegexOptions.Compiled);
    private static readonly Regex RxAssignmentFailed =
        new(@"Assignment failed for '([^']+)'", RegexOptions.Compiled);

    // -- SCCM deployments --
    private static readonly Regex RxSccmDeployments =
        new(@"Creating SCCM deployments", RegexOptions.Compiled);

    // -- Completion --
    private static readonly Regex RxIntuneComplete =
        new(@"(?:DW|Wrapp)\.Packager \(Intune\) completed", RegexOptions.Compiled);
    // Matches both the current "Wrapp.Packager (SCCM)" wording and the legacy
    // "SCCMPackager" prefix (pre-4.0 module versions still in the field).
    private static readonly Regex RxSccmComplete =
        new(@"(?:Wrapp\.Packager \(SCCM\)|SCCMPackager) completed successfully", RegexOptions.Compiled);

    // -- IntuneWin32App verbose sub-steps (no package name - applied to active package) --
    private static readonly Regex RxMetadataGathering =
        new(@"Attempting to gather additional meta data from \.intunewin", RegexOptions.Compiled);
    private static readonly Regex RxBodyConstructing =
        new(@"Start constructing basic layout", RegexOptions.Compiled);
    private static readonly Regex RxAppRegistering =
        new(@"Attempting to create Win32 app using constructed body", RegexOptions.Compiled);
    private static readonly Regex RxGraphAppCreated =
        new(@"Successfully created Win32 app with ID: (.+)", RegexOptions.Compiled);
    private static readonly Regex RxContentVersionCreated =
        new(@"Successfully created contentVersions resource", RegexOptions.Compiled);
    private static readonly Regex RxFileEntryConstructing =
        new(@"Constructing Win32 app content file body for uploading", RegexOptions.Compiled);
    private static readonly Regex RxStorageUriWaiting =
        new(@"Waiting for Intune service to process contentVersions/files request", RegexOptions.Compiled);
    private static readonly Regex RxUploadMethodNative =
        new(@"(?:Using native method|falling back to (?:using )?native method|Content size is less than)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxUploadMethodAzCopy =
        new(@"Using AzCopy\.exe method", RegexOptions.Compiled);
    private static readonly Regex RxCommitStarted =
        new(@"Waiting for Intune service to process the commit file request", RegexOptions.Compiled);
    private static readonly Regex RxCommitPolling =
        new(@"operation 'CommitFile' is in pending state \(attempt (\d+)\)", RegexOptions.Compiled);
    private static readonly Regex RxCommitVersionUpdate =
        new(@"Updating committedContentVersion property", RegexOptions.Compiled);
    private static readonly Regex RxBlobCompleted =
        new(@"Successfully created Win32 app and committed file content", RegexOptions.Compiled);

    // -- Upload progress (from PS Progress stream: [PROG:nn] ...) --
    private static readonly Regex RxUploadProgress =
        new(@"^\[PROG:(\d+)\]", RegexOptions.Compiled);

    // -- Errors --
    private static readonly Regex RxFatal =
        new(@"Fatal error: (.+)", RegexOptions.Compiled);
    private static readonly Regex RxAppFailed =
        new(@"Failed to process (?:app|SCCM package) '([^']+)'", RegexOptions.Compiled);

    /// <summary>
    /// Process a single line of log output from the PS streams.
    /// Strips prefixes added by PowerShellService and Write-Log before matching.
    /// </summary>
    public void ProcessLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return;

        // Upload progress from PS Progress stream (check BEFORE stripping prefixes)
        Match m;
        if ((m = RxUploadProgress.Match(rawLine)).Success)
        {
            Emit(PhaseId.UploadProgress, null, m.Groups[1].Value);
            return;
        }

        var msg = StripPrefixes(rawLine);
        if (string.IsNullOrWhiteSpace(msg)) return;

        // Fatal and per-app failures first (highest priority)
        if ((m = RxFatal.Match(msg)).Success)
        { Emit(PhaseId.Fatal, null, m.Groups[1].Value); return; }

        if ((m = RxAppFailed.Match(msg)).Success)
        { Emit(PhaseId.AppFailed, m.Groups[1].Value, msg); return; }

        // Auth
        if (RxAuthenticated.IsMatch(msg))
        { Emit(PhaseId.Authenticated); return; }

        // Preflight
        if (RxPreflightStarted.IsMatch(msg))
        { Emit(PhaseId.PreflightStarted); return; }
        if (RxPreflightPassed.IsMatch(msg))
        { Emit(PhaseId.PreflightPassed); return; }

        // Collision
        if (RxCollisionStarted.IsMatch(msg))
        { Emit(PhaseId.CollisionCheckStarted); return; }
        if ((m = RxCollisionDetected.Match(msg)).Success)
        { Emit(PhaseId.CollisionDetected, m.Groups[1].Value); return; }
        if ((m = RxCollisionPassed.Match(msg)).Success)
        { Emit(PhaseId.CollisionCheckPassed, null, m.Groups[1].Value); return; }

        // Wrapping
        if ((m = RxWrappingStarted.Match(msg)).Success)
        { Emit(PhaseId.WrappingStarted, m.Groups[1].Value); return; }
        if (RxWrappingCopying.IsMatch(msg))
        { Emit(PhaseId.WrappingCopying); return; }
        if (RxWrappingCopied.IsMatch(msg))
        { Emit(PhaseId.WrappingCopied); return; }
        if (RxWrappingEncoding.IsMatch(msg))
        { Emit(PhaseId.WrappingEncoding); return; }
        if (RxWrappingCompleted.IsMatch(msg))
        { Emit(PhaseId.WrappingCompleted); return; }

        // Intune per-package
        if ((m = RxAppProcessing.Match(msg)).Success)
        { Emit(PhaseId.AppProcessingStarted, m.Groups[1].Value.Trim()); return; }
        if ((m = RxAppCreating.Match(msg)).Success)
        { Emit(PhaseId.AppCreatingStarted, m.Groups[1].Value.Trim()); return; }
        if ((m = RxAppCreated.Match(msg)).Success)
        { Emit(PhaseId.AppCreated, m.Groups[1].Value.Trim()); return; }

        // IntuneWin32App module sub-steps (no package name -- applied to active package)
        if (RxMetadataGathering.IsMatch(msg))
        { Emit(PhaseId.UploadMetadata); return; }
        if (RxBodyConstructing.IsMatch(msg))
        { Emit(PhaseId.UploadConstructing); return; }
        if (RxAppRegistering.IsMatch(msg))
        { Emit(PhaseId.UploadRegistering); return; }
        if ((m = RxGraphAppCreated.Match(msg)).Success)
        { Emit(PhaseId.UploadGraphCreated, null, m.Groups[1].Value.Trim()); return; }
        if (RxContentVersionCreated.IsMatch(msg))
        { Emit(PhaseId.UploadContentVersion); return; }
        if (RxFileEntryConstructing.IsMatch(msg))
        { Emit(PhaseId.UploadFileEntry); return; }
        if (RxStorageUriWaiting.IsMatch(msg))
        { Emit(PhaseId.UploadStorageUriWaiting); return; }
        if (RxUploadMethodNative.IsMatch(msg) || RxUploadMethodAzCopy.IsMatch(msg))
        { Emit(PhaseId.UploadStarted); return; }
        if (RxCommitStarted.IsMatch(msg))
        { Emit(PhaseId.UploadCommitting); return; }
        if ((m = RxCommitPolling.Match(msg)).Success)
        { Emit(PhaseId.UploadCommitPolling, null, m.Groups[1].Value); return; }
        if (RxCommitVersionUpdate.IsMatch(msg))
        { Emit(PhaseId.UploadCommitVersionUpdate); return; }
        if (RxBlobCompleted.IsMatch(msg))
        { Emit(PhaseId.UploadCompleted); return; }

        // SCCM per-package
        if ((m = RxSccmAppCreating.Match(msg)).Success)
        { Emit(PhaseId.SccmAppCreating, m.Groups[1].Value.Trim()); return; }

        // Dependencies and supersedence
        if ((m = RxDependencies.Match(msg)).Success)
        { Emit(PhaseId.DependenciesStarted, m.Groups[1].Value); return; }
        if ((m = RxSupersedence.Match(msg)).Success)
        { Emit(PhaseId.SupersedenceStarted, m.Groups[1].Value); return; }

        // Assignments
        if ((m = RxAssignmentWaiting.Match(msg)).Success)
        { Emit(PhaseId.AssignmentWaiting, m.Groups[1].Value); return; }
        if ((m = RxAssignmentReady.Match(msg)).Success)
        { Emit(PhaseId.AssignmentReady, m.Groups[1].Value); return; }
        if ((m = RxAssignmentResult.Match(msg)).Success)
        { Emit(PhaseId.AssignmentCompleted, m.Groups[1].Value, $"{m.Groups[2].Value} applied, {m.Groups[3].Value} failed"); return; }
        if ((m = RxAssignmentFailed.Match(msg)).Success)
        { Emit(PhaseId.AssignmentFailed, m.Groups[1].Value); return; }

        // SCCM deployments
        if (RxSccmDeployments.IsMatch(msg))
        { Emit(PhaseId.SccmDeploymentsStarted); return; }

        // Completion
        if (RxIntuneComplete.IsMatch(msg) || RxSccmComplete.IsMatch(msg))
        { Emit(PhaseId.OrchestratorCompleted); return; }
    }

    private static string StripPrefixes(string line)
    {
        var msg = line;

        foreach (var prefix in new[] { "[INFO] ", "[WARN] ", "[ERROR] ", "[VERB] " })
        {
            if (msg.StartsWith(prefix, StringComparison.Ordinal))
            {
                msg = msg.Substring(prefix.Length);
                break;
            }
        }

        // Strip Write-Log timestamp: [yyyy-MM-dd HH:mm:ss]
        if (msg.Length > 22 && msg[0] == '[' && msg[20] == ']')
        {
            var inner = msg.Substring(1, 19);
            if (DateTime.TryParse(inner, out _))
                msg = msg.Substring(22).TrimStart();
        }

        if (msg.StartsWith("[INFO]", StringComparison.Ordinal))
            msg = msg.Substring(6).TrimStart();
        else if (msg.StartsWith("[ERROR]", StringComparison.Ordinal))
            msg = msg.Substring(7).TrimStart();

        return msg;
    }

    private void Emit(PhaseId phase, string? packageName = null, string? detail = null)
    {
        PhaseChanged?.Invoke(new PhaseEvent(phase, packageName, detail));
    }
}

public record PhaseEvent(PhaseId Phase, string? PackageName, string? Detail);

public enum PhaseId
{
    Authenticated,
    PreflightStarted,
    PreflightPassed,
    CollisionCheckStarted,
    CollisionDetected,
    CollisionCheckPassed,
    WrappingStarted,
    WrappingCopying,
    WrappingCopied,
    WrappingEncoding,
    WrappingCompleted,
    AppProcessingStarted,
    AppCreatingStarted,
    AppCreated,
    UploadMetadata,
    UploadConstructing,
    UploadRegistering,
    UploadGraphCreated,
    UploadContentVersion,
    UploadFileEntry,
    UploadStorageUriWaiting,
    UploadStarted,
    UploadProgress,
    UploadCommitting,
    UploadCommitPolling,
    UploadCommitVersionUpdate,
    UploadCompleted,
    SccmAppCreating,
    DependenciesStarted,
    SupersedenceStarted,
    AssignmentWaiting,
    AssignmentReady,
    AssignmentCompleted,
    AssignmentFailed,
    SccmDeploymentsStarted,
    OrchestratorCompleted,
    Fatal,
    AppFailed
}
