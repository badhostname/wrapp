using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Hosts a PowerShell RunspacePool with Wrapp.Packager loaded.
/// All packaging logic stays in PowerShell - this class is a pure C# bridge.
/// </summary>
public sealed class PowerShellService : IDisposable
{
    private RunspacePool _pool;
    private readonly string _modulePath;
    private bool _disposed;

    /// <summary>
    /// PowerShell snippet that clears every global variable this service injects.
    /// Appended to every script we run so secrets/tokens never outlive the call
    /// inside a reused runspace. Kept as a string constant so the exact names
    /// match <see cref="PowerShellTokenBridge"/>.
    /// <para>
    /// Phase 12 (S-3): the live IPCA was retired from the runspace; the new
    /// surface is the GUID handle <c>$Global:WrappMsalRefreshHandle</c>. The
    /// legacy names (<c>WrappMsalApp</c>, <c>WrappMsalAccount</c>,
    /// <c>WrappMsalScopes</c>) remain in the clear-list as defence-in-depth
    /// for runspaces that may still carry them from earlier builds in the
    /// same process lifetime.
    /// </para>
    /// </summary>
    private const string ClearInjectedGlobalsScript = @"
try {
    Remove-Variable -Scope Global -Name `
        AccessToken, AuthenticationHeader, AccessTokenTenantID, `
        WrappMsalRefreshHandle, WrappTokenMap, `
        WrappMsalApp, WrappMsalAccount, WrappMsalScopes `
        -ErrorAction SilentlyContinue
} catch { }
";

    public PowerShellService(string modulePath)
    {
        _modulePath = modulePath;

        // Self-contained deployments don't set PSHOME automatically.
        // The PS runtime needs it to locate core built-in modules (Management, Utility, etc.).
        // These modules live under runtimes/win/lib/net8.0/Modules/ in the published output,
        // so PSHOME must point to runtimes/win/lib/net8.0/ (not the app root).
        var existingPsHome = Environment.GetEnvironmentVariable("PSHOME");
        AppLogger.Info($"PowerShell: existing PSHOME = {existingPsHome ?? "(not set)"}");
        AppLogger.Info($"PowerShell: AppContext.BaseDirectory = {AppContext.BaseDirectory}");

        if (string.IsNullOrEmpty(existingPsHome))
        {
            var runtimeModules = Path.Combine(AppContext.BaseDirectory, "runtimes", "win", "lib", "net8.0");
            var runtimeModulesDir = Path.Combine(runtimeModules, "Modules");
            AppLogger.Info($"PowerShell: checking {runtimeModulesDir} -> exists={Directory.Exists(runtimeModulesDir)}");

            if (Directory.Exists(runtimeModulesDir))
                Environment.SetEnvironmentVariable("PSHOME", runtimeModules);
            else
                Environment.SetEnvironmentVariable("PSHOME", AppContext.BaseDirectory);

            AppLogger.Info($"PowerShell: PSHOME set to {Environment.GetEnvironmentVariable("PSHOME")}");
        }

        // Add module directories to PSModulePath so IntuneWin32App and other
        // dependencies are discoverable by Import-Module.
        var moduleDir = Path.GetDirectoryName(modulePath);
        var parentDir = moduleDir is not null ? Path.GetDirectoryName(moduleDir) : null;
        if (parentDir is not null)
        {
            var psModulePath = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
            var pathsToAdd = new List<string>();

            // Bundled layout: Wrapp.Packager and IntuneWin32App are siblings
            // under Modules/. Adding parentDir lets PS auto-discover them.
            if (!psModulePath.Contains(parentDir, StringComparison.OrdinalIgnoreCase))
                pathsToAdd.Add(parentDir);

            // Dev/repo layout: modules live in Script\Modules\ while
            // Wrapp.Packager lives in Script\. Add Script\Modules if it exists.
            var vendoredModules = Path.Combine(parentDir, "Modules");
            if (Directory.Exists(vendoredModules) &&
                !psModulePath.Contains(vendoredModules, StringComparison.OrdinalIgnoreCase))
                pathsToAdd.Add(vendoredModules);

            if (pathsToAdd.Count > 0)
            {
                var newPath = string.Join(";", pathsToAdd) + ";" + psModulePath;
                Environment.SetEnvironmentVariable("PSModulePath", newPath);
                AppLogger.Info($"PowerShell: PSModulePath updated, added: {string.Join(", ", pathsToAdd)}");
            }
        }

        // Ensure core PS modules (Management, Utility, etc.) are on PSModulePath.
        // On machines without PS7 installed, CreateDefault2() won't find them
        // unless we explicitly add the bundled runtimes path.
        var coreModulesPath = Path.Combine(AppContext.BaseDirectory, "runtimes", "win", "lib", "net8.0", "Modules");
        if (Directory.Exists(coreModulesPath))
        {
            var currentPath = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
            if (!currentPath.Contains(coreModulesPath, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PSModulePath",
                    coreModulesPath + ";" + currentPath);
                AppLogger.Info($"PowerShell: added core modules path: {coreModulesPath}");
            }
        }

        _pool = CreatePool();
    }

    /// <summary>
    /// Creates a fresh RunspacePool with Wrapp.Packager imported. Used by the
    /// constructor, by <see cref="RecyclePool"/> on tenant/account change, and
    /// by <see cref="BeginPackagingSession"/> for a run-scoped pool.
    /// <paramref name="maxRunspaces"/> is 1 for a run-scoped pool (tenants run
    /// sequentially, and a single runspace guarantees per-pipeline token
    /// injection lands on the same runspace the packaging script uses).
    /// </summary>
    private RunspacePool CreatePool(int maxRunspaces = 3)
    {
        var iss = InitialSessionState.CreateDefault2();
        // Embedded PS hosts default to Restricted execution policy.
        // Override so module scripts (.psm1) can load.
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        iss.ImportPSModule(new[] { _modulePath });
        // Phase 12 hardening (S-3): register the Invoke-WrappTokenRefresh
        // cmdlet so PS scripts can perform silent MSAL refresh via an opaque
        // handle without ever seeing the live IPCA. The cmdlet class lives in
        // this assembly; CreateDefault2 + this entry surfaces it in every
        // runspace the pool creates.
        iss.Commands.Add(new SessionStateCmdletEntry(
            "Invoke-WrappTokenRefresh",
            typeof(InvokeWrappTokenRefreshCommand),
            helpFileName: null));
        var pool = RunspaceFactory.CreateRunspacePool(iss);
        pool.SetMinRunspaces(1);
        pool.SetMaxRunspaces(maxRunspaces);
        pool.Open();
        return pool;
    }

    // -----------------------------------------------------------------------
    // Ambient-pool token hygiene
    // -----------------------------------------------------------------------

    /// <summary>
    /// Clears injected MSAL token globals ($Global:AccessToken /
    /// AuthenticationHeader / AccessTokenTenantID) from the ambient RunspacePool.
    /// Called on identity change and sign-out so a switched-away / signed-out
    /// token can't linger in the pool.
    ///
    /// <para>Cheap: runs the clear script; does NOT dispose/recreate the pool
    /// (unlike the former <c>RecyclePool</c>). Every consumer injects its own
    /// token at point of use -- inventory per call (<see cref="RunScriptWithTokenAsync"/>),
    /// packaging per pipeline on its own run-scoped pool -- so this is
    /// hygiene/defence-in-depth, not correctness-critical. This replaces the old
    /// eager sign-in pool seeding (RecyclePool + InjectMsalToken), which coupled
    /// the account UI to the runspace pool a packaging run was using.</para>
    /// </summary>
    public void ClearAmbientPoolToken()
    {
        if (_disposed) return;
        try
        {
            using var ps = PowerShell.Create();
            ps.RunspacePool = _pool;
            ps.AddScript(ClearInjectedGlobalsScript);
            ps.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"PowerShell: ClearAmbientPoolToken failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Packaging run pool lifecycle
    //
    // A packaging run gets its OWN dedicated RunspacePool (B″) via
    // BeginPackagingSession, so the ambient pool (inventory / account) is fully
    // decoupled from the run. The depth counter drives create-on-first-entry /
    // dispose-on-last-exit of the run pool; IsPackagingActive is retained as a
    // read-only signal.
    // -----------------------------------------------------------------------

    private int _packagingDepth;

    /// <summary>
    /// Dedicated single-runspace pool owned by the active packaging run (B″).
    /// Packaging uses this instead of the ambient <see cref="_pool"/> so an
    /// account/identity switch (which recycles/disposes the ambient pool) can't
    /// pull the pool out from under an in-flight run. Null when no run is active.
    /// A single run at a time is guaranteed by <c>RunViewModel.IsBusy</c>.
    /// </summary>
    private RunspacePool? _runPool;

    /// <summary>True while a packaging run holds its dedicated RunspacePool.</summary>
    public bool IsPackagingActive => System.Threading.Volatile.Read(ref _packagingDepth) > 0;

    /// <summary>
    /// Begins a packaging run: creates a dedicated run-scoped RunspacePool that
    /// <see cref="PackageAsync"/> uses for the run's lifetime, disposed when the
    /// returned scope is disposed (in <c>RunViewModel</c>'s finally, after the
    /// tenant loop and cancellation drain — so all pipelines have stopped first).
    /// Reentrant/thread-safe via an interlocked depth counter; the pool is
    /// created on first entry and disposed on last exit.
    /// </summary>
    public IDisposable BeginPackagingSession()
    {
        if (System.Threading.Interlocked.Increment(ref _packagingDepth) == 1)
        {
            // Single runspace: tenants run sequentially, and it guarantees the
            // per-pipeline token injection and the packaging script share the
            // same runspace (no pool-runspace-reuse ambiguity).
            _runPool = CreatePool(maxRunspaces: 1);
        }
        return new PackagingSession(this);
    }

    private sealed class PackagingSession : IDisposable
    {
        private readonly PowerShellService _owner;
        private bool _disposed;
        internal PackagingSession(PowerShellService owner) => _owner = owner;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (System.Threading.Interlocked.Decrement(ref _owner._packagingDepth) == 0)
            {
                var pool = _owner._runPool;
                _owner._runPool = null;
                try { pool?.Close(); pool?.Dispose(); }
                catch (Exception ex) { AppLogger.Warn($"PowerShell: run pool dispose failed: {ex.Message}"); }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Startup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Imports Wrapp.Packager into the pool and returns true if successful.
    /// Call once on app startup.
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            AppLogger.Info($"PowerShell: importing module from {_modulePath}");
            var script = $"Import-Module '{_modulePath.Replace("'", "''")}' -Force -ErrorAction Stop";
            var results = await InvokeAsync(script);
            AppLogger.Info("PowerShell: module loaded successfully");
            return true;
        }
        catch (Exception ex)
        {
            LastInitError = ex.Message;
            AppLogger.Exception("PowerShell: module load failed", ex);
            return false;
        }
    }

    public string? LastInitError { get; private set; }

    // -----------------------------------------------------------------------
    // Module defaults
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads ValidXxx arrays from Wrapp.Packager's Defaults.psd1 via the module's
    /// $script:ModuleDefaults variable. Returns fallback empty values if unavailable.
    /// </summary>
    public async Task<ModuleDefaults> LoadDefaultsAsync()
    {
        try
        {
            var script = @"
$mod = Get-Module Wrapp.Packager
if (-not $mod) { return $null }
& $mod {
    $d = $script:ModuleDefaults
    @{
        ValidAuthFlows                = @($d.ValidAuthFlows)
        ValidInstallExperience        = @($d.ValidInstallExperience)
        ValidRestartBehavior          = @($d.ValidRestartBehavior)
        ValidArchitecture             = @($d.ValidArchitecture)
        ValidMinOS                    = @($d.ValidMinOS)
        ValidUpdateModes              = @($d.ValidUpdateModes)
        ValidDetectionTypes           = @($d.ValidDetectionTypes)
        ValidRequirementTypes         = @($d.ValidRequirementTypes)
        ValidReturnCodeTypes          = @($d.ValidReturnCodeTypes)
        ValidDependencyTypes          = @($d.ValidDependencyTypes)
        ValidSupersedenceTypes        = @($d.ValidSupersedenceTypes)
        ValidAssignmentIntents        = @($d.ValidAssignmentIntents)
        ValidAssignmentTypes          = @($d.ValidAssignmentTypes)
        ValidFilterModes              = @($d.ValidFilterModes)
        ValidInstallationBehaviorTypes = @($d.ValidInstallationBehaviorTypes)
        ValidLogonRequirementTypes    = @($d.ValidLogonRequirementTypes)
        ValidUserNotifications        = @($d.ValidUserNotifications)
        ValidDeployActions            = @($d.ValidDeployActions)
        ValidDeployPurposes           = @($d.ValidDeployPurposes)
    }
}
";
            var results = await InvokeAsync(script);
            if (results.Count == 0 || results[0].BaseObject is not System.Collections.Hashtable ht)
                return ModuleDefaults.Empty;

            return new ModuleDefaults(
                ValidAuthFlows:                 ToEnumArray<AuthFlow>(ht["ValidAuthFlows"]),
                ValidInstallExperience:         ToStringArray(ht["ValidInstallExperience"]),
                ValidRestartBehavior:           ToStringArray(ht["ValidRestartBehavior"]),
                ValidArchitecture:              ToStringArray(ht["ValidArchitecture"]),
                ValidMinOS:                     ToStringArray(ht["ValidMinOS"]),
                ValidUpdateModes:               ToEnumArray<UpdateMode>(ht["ValidUpdateModes"]),
                ValidDetectionTypes:            ToStringArray(ht["ValidDetectionTypes"]),
                ValidRequirementTypes:          ToStringArray(ht["ValidRequirementTypes"]),
                ValidReturnCodeTypes:           ToStringArray(ht["ValidReturnCodeTypes"]),
                ValidDependencyTypes:           ToStringArray(ht["ValidDependencyTypes"]),
                ValidSupersedenceTypes:         ToStringArray(ht["ValidSupersedenceTypes"]),
                ValidAssignmentIntents:         ToStringArray(ht["ValidAssignmentIntents"]),
                ValidAssignmentTypes:           ToStringArray(ht["ValidAssignmentTypes"]),
                ValidFilterModes:               ToStringArray(ht["ValidFilterModes"]),
                ValidGroupModes:                ToStringArray(ht["ValidGroupModes"]),
                ValidIntuneNotifications:       ToStringArray(ht["ValidIntuneNotifications"]),
                ValidDeliveryOptimizationPriorities: ToStringArray(ht["ValidDeliveryOptimizationPriorities"]),
                ValidInstallationBehaviorTypes: ToStringArray(ht["ValidInstallationBehaviorTypes"]),
                ValidLogonRequirementTypes:     ToStringArray(ht["ValidLogonRequirementTypes"]),
                ValidUserNotifications:         ToStringArray(ht["ValidUserNotifications"]),
                ValidDeployActions:             ToStringArray(ht["ValidDeployActions"]),
                ValidDeployPurposes:            ToStringArray(ht["ValidDeployPurposes"]),
                ValidSlowNetworkDeploymentModes: ToStringArray(ht["ValidSlowNetworkDeploymentModes"]),
                ValidUserInteractionModes:       ToStringArray(ht["ValidUserInteractionModes"]),
                ValidRebootBehaviors:            ToStringArray(ht["ValidRebootBehaviors"]),
                ValidTimeBaseOn:                 ToStringArray(ht["ValidTimeBaseOn"])
            ).WithFallbacks();
        }
        catch
        {
            return ModuleDefaults.Empty;
        }
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Calls Test-WrappConfig and returns structured Issues.
    /// </summary>
    public async Task<IReadOnlyList<ValidationIssue>> ValidateConfigAsync(
        string configPath, string scriptType = "IntunePackager")
    {
        var escaped = configPath.Replace("'", "''");
        var importCmd = $"Import-Module '{_modulePath.Replace("'", "''")}' -Force -ErrorAction Stop; ";
        var script = importCmd + $@"
try {{
    $result = Test-WrappConfig -ConfigPath '{escaped}' -ScriptType '{scriptType}'
    if ($result.Issues) {{ $result.Issues }} else {{ @() }}
}} finally {{
    {ClearInjectedGlobalsScript}
}}
";
        var results = await InvokeAsync(script);
        return results
            .Select(r => MapIssue(r))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();
    }

    // -----------------------------------------------------------------------
    // Run / package
    // -----------------------------------------------------------------------

    /// <summary>
    /// Calls Invoke-WrappIntune or Invoke-WrappSccm and streams log lines.
    /// </summary>
    public async Task<bool> PackageAsync(
        string configPath,
        string target,
        string tenantKey,
        IProgress<string> log,
        CancellationToken ct,
        string[]? packageNames = null,
        string? siteCode = null,
        Models.MsalTokenResult? msalToken = null,
        Microsoft.Identity.Client.IPublicClientApplication? msalApp = null,
        Microsoft.Identity.Client.IAccount? msalAccount = null,
        Action<PSObject>? onOutput = null)
    {
        // Defence in depth: reject any tenantKey that isn't a GUID at the API
        // boundary. Upstream callers already filter, but this prevents any
        // future code path from splicing arbitrary strings into the PS script.
        if (!string.IsNullOrWhiteSpace(tenantKey) && !Guid.TryParse(tenantKey, out _))
        {
            AppLogger.Warn($"PowerShell: rejecting non-GUID tenantKey '{tenantKey}'");
            log.Report("[ERROR] Invalid tenant identifier; refusing to run.");
            return false;
        }

        var escaped = configPath.Replace("'", "''");
        string cmdlet = target.Equals("SCCM", StringComparison.OrdinalIgnoreCase)
            ? "Invoke-WrappSccm"
            : "Invoke-WrappIntune";

        var tenantParam = string.IsNullOrWhiteSpace(tenantKey)
            ? string.Empty
            : $" -TenantId '{tenantKey.Replace("'", "''")}'";

        var siteParam = string.IsNullOrWhiteSpace(siteCode)
            ? string.Empty
            : $" -SiteCode '{siteCode.Replace("'", "''")}'";

        var pkgParam = string.Empty;
        if (packageNames is { Length: > 0 })
        {
            var quoted = string.Join(",", packageNames.Select(n => $"'{n.Replace("'", "''")}'"));
            pkgParam = $" -PackageNames @({quoted})";
        }

        // Keep verbose SILENT globally and scope -Verbose to the single call that
        // PhaseDetector cares about (Add-IntuneWin32App, inside Add-IntuneWin32AppFromConfig).
        // Enabling verbose globally floods the log with every Graph/REST call from
        // preflight, collision detection, and token refresh -- slows the run and
        // buries the upload-phase markers. The cmdlet-level -Verbose switch on
        // Add-IntuneWin32App overrides this preference for just that invocation,
        // so PhaseDetector still sees the 13 upload sub-step strings it tracks.
        //
        // ClearInjectedGlobalsScript is appended in a `finally` so tokens and the
        // live MSAL app object never persist in the runspace after this call.
        var importCmd = $"Import-Module '{_modulePath.Replace("'", "''")}' -Force -ErrorAction Stop; ";
        var verboseCmd = "$VerbosePreference = 'SilentlyContinue'; ";
        var invokeCmd  = $"{cmdlet} -ConfigPath '{escaped}'{tenantParam}{siteParam}{pkgParam}";
        var script = importCmd + verboseCmd + $"try {{ {invokeCmd} }} finally {{ {ClearInjectedGlobalsScript} }}";

        return await RunPackagingScriptAsync(script, InjectTokens, log, ct, onOutput);

        // Inject MSAL app object + token into THIS runspace before running.
        // The app object enables mid-run AcquireTokenSilent for long operations.
        List<Guid> InjectTokens(PowerShell ps)
        {
            var handles = new List<Guid>();
            if (msalToken is not null)
            {
                if (msalApp is not null)
                {
                    // Phase 12 (S-3): the registered refresh handle must be
                    // released after the run regardless of how the pipeline
                    // ends -- the runner unregisters everything we return.
                    handles.Add(PowerShellTokenBridge.InjectMsalApp(
                        ps, msalApp, msalAccount,
                        MsalAuthService.DelegatedScopes, msalToken));
                }
                else
                {
                    // Fallback: token-only injection (no refresh capability).
                    // Uses the bridge's shared globals body so the injected shape
                    // can't drift from the refresh path above.
                    ps.AddScript("param($Token, $TenantId)\n" + PowerShellTokenBridge.TokenGlobalsBody);
                    ps.AddParameter("Token", PowerShellTokenBridge.BuildTokenPSObject(msalToken));
                    ps.AddParameter("TenantId", msalToken.TenantId);
                    ps.Invoke();
                    ps.Commands.Clear();
                }
            }
            return handles;
        }
    }

    /// <summary>
    /// Workstream P: runs the module-owned multi-tenant Intune orchestrator
    /// (<c>Invoke-WrappPackaging</c>) once for the whole run. The module owns
    /// the tenant loop (grouping packages by their config <c>TenantId</c>),
    /// so UI and CLI share the same orchestration code path by construction.
    /// Per-tenant tokens travel as a single injected map (see
    /// <see cref="PowerShellTokenBridge.InjectMsalTokenMap"/>); tenants
    /// without an entry self-authenticate from the config's AuthFlow.
    /// </summary>
    public async Task<bool> PackageAllTenantsAsync(
        string configPath,
        IReadOnlyList<string> tenantIds,
        IProgress<string> log,
        CancellationToken ct,
        IReadOnlyList<(Models.MsalTokenResult Token, Microsoft.Identity.Client.IAccount? Account)>? tokenEntries = null,
        Microsoft.Identity.Client.IPublicClientApplication? msalApp = null,
        Action<PSObject>? onOutput = null,
        string[]? packageNames = null,
        bool skipCollisionCheck = false)
    {
        // Same defence-in-depth as PackageAsync: every tenant id spliced into
        // the script must be a GUID.
        foreach (var id in tenantIds)
        {
            if (!Guid.TryParse(id, out _))
            {
                AppLogger.Warn($"PowerShell: rejecting non-GUID tenant id '{id}'");
                log.Report("[ERROR] Invalid tenant identifier; refusing to run.");
                return false;
            }
        }

        var escaped = configPath.Replace("'", "''");
        var tenantFilter = tenantIds.Count > 0
            ? $" -TenantIds @({string.Join(",", tenantIds.Select(t => $"'{t}'"))})"
            : string.Empty;

        var pkgFilter = packageNames is { Length: > 0 }
            ? $" -PackageNames @({string.Join(",", packageNames.Select(n => $"'{n.Replace("'", "''")}'"))})"
            : string.Empty;

        // Only when the caller has JUST run Test-Win32AppCollisions itself
        // (the pre-flight dialog) -- otherwise the module must keep its own check.
        var skipFlag = skipCollisionCheck ? " -SkipCollisionCheck" : string.Empty;

        var importCmd  = $"Import-Module '{_modulePath.Replace("'", "''")}' -Force -ErrorAction Stop; ";
        var verboseCmd = "$VerbosePreference = 'SilentlyContinue'; ";
        var invokeCmd  = $"Invoke-WrappPackaging -ConfigPath '{escaped}' -Target Intune{tenantFilter}{pkgFilter}{skipFlag}";
        var script = importCmd + verboseCmd + $"try {{ {invokeCmd} }} finally {{ {ClearInjectedGlobalsScript} }}";

        return await RunPackagingScriptAsync(script, InjectTokens, log, ct, onOutput);

        List<Guid> InjectTokens(PowerShell ps)
            => msalApp is not null && tokenEntries is { Count: > 0 }
                ? PowerShellTokenBridge.InjectMsalTokenMap(
                    ps, msalApp, MsalAuthService.DelegatedScopes, tokenEntries)
                : new List<Guid>();
    }

    /// <summary>
    /// Shared packaging pipeline runner: creates the PowerShell instance on the
    /// run-scoped pool, applies the caller's token injection, wires all five
    /// PS streams (redacted) + the live output tap, executes with
    /// cancellation + child-process cleanup, resolves the run outcome from the
    /// script's own result object, and guarantees post-run token hygiene
    /// (globals cleared, every registered MSAL refresh handle released).
    /// </summary>
    private async Task<bool> RunPackagingScriptAsync(
        string script,
        Func<PowerShell, List<Guid>>? injectTokens,
        IProgress<string> log,
        CancellationToken ct,
        Action<PSObject>? onOutput)
    {
        using var ps = PowerShell.Create();
        // B″: packaging uses the run-scoped pool (isolated from account switches);
        // falls back to the ambient pool only if no session is active (shouldn't happen).
        ps.RunspacePool = _runPool ?? _pool;

        // Every MSAL refresh handle the injection registers must be released
        // after the run regardless of how the pipeline ends (Phase 12 S-3).
        var msalRefreshHandles = injectTokens?.Invoke(ps) ?? new List<Guid>();

        ps.AddScript(script);

        // SEC-5 (2026-07 audit): route every PS stream through AppLogger.Redact
        // before it reaches the UI/log sink. These handlers surface raw
        // exception/verbose text (e.g. a failed Invoke-RestMethod can echo an
        // "Authorization: Bearer ..." header). The PS-side Write-Log scrubs its
        // own output, but these C# handlers previously did not, so a Bearer /
        // JWT / client_secret in a native error could leak to app.log.
        ps.Streams.Information.DataAdded += (_, e) =>
            log.Report(AppLogger.Redact($"[INFO] {ps.Streams.Information[e.Index].MessageData}"));
        ps.Streams.Warning.DataAdded += (_, e) =>
            log.Report(AppLogger.Redact($"[WARN] {ps.Streams.Warning[e.Index].Message}"));
        ps.Streams.Error.DataAdded += (_, e) =>
            log.Report(AppLogger.Redact($"[ERROR] {ps.Streams.Error[e.Index].Exception?.Message ?? ps.Streams.Error[e.Index].ToString()}"));
        ps.Streams.Verbose.DataAdded += (_, e) =>
            log.Report(AppLogger.Redact($"[VERB] {ps.Streams.Verbose[e.Index].Message}"));
        ps.Streams.Progress.DataAdded += (_, e) =>
        {
            var rec = ps.Streams.Progress[e.Index];
            if (rec.PercentComplete >= 0)
                log.Report($"[PROG:{rec.PercentComplete}] {rec.Activity}: {rec.StatusDescription}");
        };

        // Capture run-start so cancel-path only kills processes WE spawned.
        // Without this, KillChildProcesses would enumerate every robocopy /
        // IntuneWinAppUtil / AzCopy system-wide — including unrelated processes
        // owned by other users on a shared terminal server.
        var runStartUtc = SystemClock.UtcNow.AddSeconds(-1); // small guard band for clock skew

        try
        {
            var output = new PSDataCollection<PSObject>();
            if (onOutput is not null)
                output.DataAdded += (_, e) => { if (output[e.Index] is PSObject obj) onOutput(obj); };

            var asyncResult = ps.BeginInvoke<PSObject, PSObject>(null, output);
            while (!asyncResult.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);
            }
            ps.EndInvoke(asyncResult);

            // Trust the script's own $Result.Success over ps.HadErrors.
            // HadErrors flips true if any cmdlet writes to the error stream,
            // including ConfigMgr's deprecation messages for Owner /
            // SupportContact (which complete successfully but emit error
            // records anyway). Invoke-{SCCM,Intune}Packager returns a
            // PSCustomObject with .Success / .Errors that reflects the real
            // run outcome -- find it in the output collection and use it.
            // Iterate in reverse because the cmdlet emits phase event
            // objects throughout the run; the actual $Result is the last
            // PSObject returned.
            for (int i = output.Count - 1; i >= 0; i--)
            {
                var obj = output[i];
                if (obj?.Properties["Success"]?.Value is bool reportedSuccess)
                {
                    if (!reportedSuccess && !ps.HadErrors)
                    {
                        // Script said fail but engine saw no errors --
                        // surface a hint so debugging starts in the right place.
                        AppLogger.Info("PowerShell: packager reported Success=false even though ps.HadErrors=false; check $Result.Errors in the host script.");
                    }
                    else if (reportedSuccess && ps.HadErrors)
                    {
                        AppLogger.Info("PowerShell: packager reported Success=true; ignoring ps.HadErrors (likely cmdlet deprecation messages on the error stream).");
                    }
                    return reportedSuccess;
                }
            }

            // Fall back to HadErrors when the script returned no result
            // (script crashed without producing $Result, or a non-packager
            // script was invoked here). Mirrors the original behaviour.
            return ps.HadErrors == false;
        }
        catch (OperationCanceledException)
        {
            ps.Stop();
            KillChildProcesses(runStartUtc);
            log.Report("[CANCELLED] Run cancelled by user. Sub-processes stopped.");
            return false;
        }
        finally
        {
            // Phase 12 hardening (S-8 + S-3): always clean up injected
            // globals from the runspace state AND release the MSAL refresh
            // handle, even on cancel / Stop / unexpected throw.
            //
            // The inline PS script already wraps the cmdlet call in a
            // try/finally so the normal path clears the globals before
            // returning to BeginInvoke. The C#-side cleanup here is the belt
            // for the cases that finally doesn't cover: ps.Stop() on cancel,
            // BeginInvoke crashing before the script enters its try, the
            // runspace dying mid-pipeline. We use a fresh PowerShell instance
            // so the failing pipeline's state cannot block cleanup.
            try
            {
                using var clearPs = PowerShell.Create();
                // B″: clear globals on the SAME pool the packaging ran on (the
                // run pool), so tokens don't linger between tenants in a run.
                clearPs.RunspacePool = _runPool ?? _pool;
                clearPs.AddScript(ClearInjectedGlobalsScript);
                clearPs.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"PowerShell: post-run cleanup of injected globals failed: {ex.Message}");
            }

            foreach (var handle in msalRefreshHandles)
            {
                MsalRefreshRegistry.Unregister(handle);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Child process cleanup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Known executables spawned by Wrapp.Packager / IntuneWin32App scripts.
    /// These are killed on cancel and on dispose to prevent orphaned processes.
    /// </summary>
    private static readonly HashSet<string> ChildProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "robocopy",
        "IntuneWinAppUtil",
        "AzCopy"
    };

    /// <summary>
    /// Kills known child processes that may have been spawned by packaging scripts.
    /// Safe to call multiple times -- already-exited processes are silently skipped.
    ///
    /// <para>Scoped to the CURRENT Windows session AND to processes started
    /// after <paramref name="runStartUtc"/>. This prevents us from killing
    /// unrelated robocopy / IntuneWinAppUtil / AzCopy instances owned by
    /// other users on a shared terminal server, or instances started by a
    /// previous Wrapp run / a different tool running on the same machine.</para>
    ///
    /// <para>Pass <see cref="DateTime.MinValue"/> to fall back to the old
    /// unscoped behaviour (kill-everything-matching). Only used on Dispose
    /// as a last-resort cleanup; the cancel path always passes run-start.</para>
    /// </summary>
    private static void KillChildProcesses(DateTime runStartUtc)
    {
        try
        {
            var currentSession = Process.GetCurrentProcess().SessionId;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (!ChildProcessNames.Contains(proc.ProcessName))
                        continue;

                    // Session scoping: only processes in the same Windows session.
                    if (proc.SessionId != currentSession)
                        continue;

                    // Time scoping: only processes spawned after this run began.
                    // If MinValue, fall through (unscoped, Dispose path).
                    if (runStartUtc != DateTime.MinValue)
                    {
                        DateTime started;
                        try { started = proc.StartTime.ToUniversalTime(); }
                        catch { continue; /* access denied on system procs */ }

                        if (started < runStartUtc) continue;
                    }

                    AppLogger.Info($"PowerShell: killing child process {proc.ProcessName} (PID {proc.Id})");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                catch { /* process may have already exited or access denied */ }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"PowerShell: child process cleanup failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // SCCM connectivity check
    // -----------------------------------------------------------------------

    /// <summary>
    /// Checks whether the ConfigurationManager PowerShell module is available
    /// and returns a lightweight connectivity result.
    /// </summary>
    public async Task<SccmConnectivityResult> TestSccmConnectivityAsync()
    {
        try
        {
            var script = @"
$adminPath = $env:SMS_ADMIN_UI_PATH
if (-not $adminPath) {
    [PSCustomObject]@{ Available = $false; Reason = 'SMS_ADMIN_UI_PATH not set'; SiteCode = ''; ServerName = '' }
    return
}
$cmModule = Join-Path (Split-Path $adminPath -Parent) 'ConfigurationManager.psd1'
if (-not (Test-Path $cmModule)) {
    [PSCustomObject]@{ Available = $false; Reason = 'ConfigurationManager.psd1 not found'; SiteCode = ''; ServerName = '' }
    return
}

# Try to detect site code and server from the CM console MRU registry
$siteCode  = ''
$serverName = ''
$mruBase = 'HKCU:\Software\Microsoft\ConfigMgr10\AdminUI\MRU'
$mruKeys = Get-ChildItem $mruBase -ErrorAction SilentlyContinue | Sort-Object Name
foreach ($k in $mruKeys) {
    $sc = (Get-ItemProperty $k.PSPath -Name 'SiteCode' -ErrorAction SilentlyContinue).SiteCode
    $sn = (Get-ItemProperty $k.PSPath -Name 'ServerName' -ErrorAction SilentlyContinue).ServerName
    if ($sc) { $siteCode = $sc; $serverName = $sn; break }
}

# Fallback: try WMI provider info registry key
if (-not $siteCode) {
    $provKey = 'HKLM:\SOFTWARE\Microsoft\SMS\Identification'
    $sc = (Get-ItemProperty $provKey -Name 'Site Code' -ErrorAction SilentlyContinue).'Site Code'
    $sn = (Get-ItemProperty $provKey -Name 'Site Server' -ErrorAction SilentlyContinue).'Site Server'
    if ($sc) { $siteCode = $sc; $serverName = $sn }
}

[PSCustomObject]@{
    Available  = $true
    Reason     = ''
    SiteCode   = if ($siteCode) { $siteCode } else { 'Module found' }
    ServerName = if ($serverName) { $serverName } else { '' }
}
";
            var results = await InvokeAsync(script);
            if (results.Count == 0)
                return new SccmConnectivityResult(false, "No result returned", null, null);

            var obj = results[0];
            var avail = obj.Properties["Available"]?.Value is bool b && b;
            var reason = obj.Properties["Reason"]?.Value?.ToString() ?? "";
            var site = obj.Properties["SiteCode"]?.Value?.ToString();
            var server = obj.Properties["ServerName"]?.Value?.ToString();
            return new SccmConnectivityResult(avail, reason, site, server);
        }
        catch (Exception ex)
        {
            // Log the full stack trace; the returned result intentionally
            // surfaces only ex.Message to the user (the Reason field is
            // shown in the connection card UI), so the stack would be lost
            // to silent disk-only consumers without this line.
            AppLogger.Exception("PowerShellService.TestSccmConnectivity", ex);
            return new SccmConnectivityResult(false, ex.Message, null, null);
        }
    }

    /// <summary>
    /// Runs an arbitrary PowerShell script in the pool with Import-Module prepended.
    /// Used by AppInventoryService and other callers that need raw PS results.
    /// </summary>
    public async Task<System.Collections.ObjectModel.Collection<PSObject>> RunScriptAsync(string script)
    {
        var importCmd = $"Import-Module '{_modulePath.Replace("'", "''")}' -ErrorAction SilentlyContinue; ";
        return await InvokeAsync(importCmd + script);
    }

    /// <summary>
    /// Like <see cref="RunScriptAsync"/> but also returns the PowerShell error
    /// stream. <c>ps.Invoke()</c> does NOT throw for non-terminating errors --
    /// they land in Streams.Error, which <see cref="RunScriptAsync"/> discards.
    /// That made "site unreachable" indistinguishable from "site has no apps"
    /// (both return an empty result set). Callers that need to tell failure
    /// from emptiness use this variant and inspect <c>Errors</c>.
    /// </summary>
    public async Task<(System.Collections.ObjectModel.Collection<PSObject> Results, List<string> Errors)>
        RunScriptWithErrorsAsync(string script)
    {
        var importCmd = $"Import-Module '{_modulePath.Replace("'", "''")}' -ErrorAction SilentlyContinue; ";
        return await Task.Run(() =>
        {
            using var ps = PowerShell.Create();
            ps.RunspacePool = _pool;
            ps.AddScript(importCmd + script);
            var results = ps.Invoke();
            var errors = ps.Streams.Error.Select(e => e.ToString()).ToList();
            return (results, errors);
        });
    }

    /// <summary>
    /// Runs a script with MSAL token injected into the SAME PowerShell instance.
    /// This guarantees $Global:AuthenticationHeader is visible to the script,
    /// unlike RunScriptAsync where injection and execution may hit different runspaces.
    /// </summary>
    /// <summary>
    /// Pre-flight collision check for the deployment-plan dialog: asks Intune
    /// whether an app with each package's display name already exists, BEFORE
    /// the run starts (and therefore before the expensive .intunewin wrap).
    /// <para>
    /// Calls the module's own exported <c>Test-Win32AppCollisions</c> — the
    /// same function the run uses — so the pre-flight and the authoritative
    /// in-run check can never disagree. Advisory only: the module still checks
    /// again during the run.
    /// </para>
    /// <para>Packages already in Update/UpdateContent mode are excluded by the
    /// function itself (a name match IS the app being updated).</para>
    /// </summary>
    public async Task<IReadOnlyList<Models.IntuneCollision>> TestIntuneCollisionsAsync(
        IReadOnlyList<(string AppName, string UpdateMode)> packages,
        Models.MsalTokenResult token)
    {
        if (packages.Count == 0) return Array.Empty<Models.IntuneCollision>();

        var entries = string.Join(",\n", packages.Select(p =>
            $"[pscustomobject]@{{ AppName = '{p.AppName.Replace("'", "''")}'; " +
            $"UpdateMode = '{p.UpdateMode.Replace("'", "''")}' }}"));

        var script =
            $"Import-Module '{_modulePath.Replace("'", "''")}' -Force -ErrorAction Stop; " +
            "$VerbosePreference = 'SilentlyContinue'; " +
            $"$pkgs = @(\n{entries}\n); " +
            "(Test-Win32AppCollisions -PackageList $pkgs).Collisions";

        var results = await RunScriptWithTokenAsync(script, token);
        var collisions = new List<Models.IntuneCollision>();
        foreach (var obj in results)
        {
            if (obj is null) continue;
            collisions.Add(new Models.IntuneCollision(
                Str(obj, "PackageName"),
                Str(obj, "ExistingAppName"),
                Str(obj, "Version"),
                Str(obj, "Publisher"),
                Str(obj, "ID")));
        }
        return collisions;

        static string Str(PSObject o, string name)
            => o.Properties[name]?.Value?.ToString() ?? string.Empty;
    }

    public async Task<System.Collections.ObjectModel.Collection<PSObject>> RunScriptWithTokenAsync(
        string script, Models.MsalTokenResult token)
    {
        return await Task.Run(() =>
        {
            using var ps = PowerShell.Create();
            ps.RunspacePool = _pool;

            // Step 1: inject token globals into THIS PS instance's runspace.
            // SEC-13 (2026-07 audit): pass the token's REAL expiry, not a
            // fabricated (Get-Date).AddMinutes(55). A hardcoded future expiry
            // makes the PS-side refresh logic skip a genuinely-near-expiry token
            // and 401 mid-run.
            ps.AddScript(@"
param($TokenVal, $TenantId, $ExpiresOn)
$Global:AuthenticationHeader = @{
    'Content-Type' = 'application/json'
    'Authorization' = 'Bearer ' + $TokenVal
    'ExpiresOn' = $ExpiresOn
}
$Global:AccessTokenTenantID = $TenantId
");
            ps.AddParameter("TokenVal", token.AccessToken);
            ps.AddParameter("TenantId", token.TenantId);
            ps.AddParameter("ExpiresOn", token.ExpiresOnUtc.ToString("o"));
            ps.Invoke();
            ps.Commands.Clear();

            // Step 2: run the actual script with verbose suppressed.
            // The Wrapp.Packager module emits verbose output during REST calls
            // which slows down inventory queries substantially.
            // The outer finally also clears token globals so a later unrelated
            // script in the same runspace can't inherit them.
            ps.AddScript($@"
$oldVerbose = $VerbosePreference
$VerbosePreference = 'SilentlyContinue'
try {{
{script}
}} finally {{
    $VerbosePreference = $oldVerbose
    {ClearInjectedGlobalsScript}
}}");
            var result = ps.Invoke();

            // Log PS warnings and errors so callers have visibility
            foreach (var warn in ps.Streams.Warning)
                AppLogger.Info($"PS warning: {warn.Message}");
            if (ps.HadErrors && ps.Streams.Error.Count > 0)
            {
                foreach (var err in ps.Streams.Error)
                    AppLogger.Warn($"PS error: {err.Exception?.Message ?? err.ToString()}");
            }

            return result;
        });
    }

    // -----------------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------------

    private async Task<System.Collections.ObjectModel.Collection<PSObject>> InvokeAsync(string script)
    {
        return await Task.Run(() =>
        {
            using var ps = PowerShell.Create();
            ps.RunspacePool = _pool;
            ps.AddScript(script);
            return ps.Invoke();
        });
    }

    private static ValidationIssue? MapIssue(PSObject obj)
    {
        if (obj?.BaseObject is null) return null;
        try
        {
            string Get(string name) =>
                obj.Properties[name]?.Value?.ToString() ?? string.Empty;

            string[] GetArray(string name)
            {
                var val = obj.Properties[name]?.Value;
                if (val is null) return Array.Empty<string>();
                if (val is object[] arr) return arr.Select(x => x?.ToString() ?? "").ToArray();
                if (val is System.Collections.IEnumerable en)
                    return en.Cast<object>().Select(x => x?.ToString() ?? "").ToArray();
                return new[] { val.ToString() ?? "" };
            }

            return new ValidationIssue(
                FieldPath:      Get("FieldPath"),
                Severity:       Get("Severity"),
                Code:           Get("Code"),
                Message:        Get("Message"),
                AttemptedValue: obj.Properties["AttemptedValue"]?.Value?.ToString(),
                AllowedValues:  GetArray("AllowedValues"),
                Guidance:       Get("Guidance")
            );
        }
        catch
        {
            return null;
        }
    }

    private static string[] ToStringArray(object? value)
    {
        if (value is null) return Array.Empty<string>();
        if (value is object[] arr) return arr.Select(x => x?.ToString() ?? "").ToArray();
        if (value is System.Collections.IEnumerable en)
            return en.Cast<object>().Select(x => x?.ToString() ?? "").ToArray();
        return new[] { value.ToString() ?? "" };
    }

    /// <summary>
    /// Parses a PowerShell-returned array of strings into a strongly-typed
    /// enum array, discarding entries that don't match any enum member.
    /// Case-insensitive; lets the PS side return canonical casing without
    /// us having to mirror every change here. Returns an empty array on
    /// null / malformed input so the downstream <c>WithFallbacks</c> call
    /// can supply defaults.
    /// </summary>
    private static T[] ToEnumArray<T>(object? value) where T : struct, Enum
    {
        var strings = ToStringArray(value);
        if (strings.Length == 0) return Array.Empty<T>();
        var result = new List<T>(strings.Length);
        foreach (var s in strings)
            if (Enum.TryParse<T>(s, ignoreCase: true, out var v))
                result.Add(v);
        return result.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Dispose path: last-resort cleanup. Still session-scoped (set inside
        // KillChildProcesses) but time-scoping is disabled via MinValue since
        // we've lost track of which run spawned what by this point.
        KillChildProcesses(DateTime.MinValue);
        // Safety net: dispose a leaked run pool (a run that threw before its
        // session scope disposed). Normally the session already cleared this.
        try { _runPool?.Close(); _runPool?.Dispose(); } catch { /* ignore */ }
        _runPool = null;
        _pool.Close();
        _pool.Dispose();
    }
}

public record SccmConnectivityResult(bool Available, string? Reason, string? SiteCode, string? ServerName);
