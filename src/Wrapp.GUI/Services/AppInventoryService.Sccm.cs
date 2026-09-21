using System.Management.Automation;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// SCCM (ConfigMgr) inventory operations for <see cref="AppInventoryService"/>.
/// Owns the per-site app summary cache reads and the per-app detail
/// fetches that hit the ConfigMgr cmdlets via PowerShellService.
/// Detail results land in the shared <c>_detailCache</c>; SCCM list results
/// land in <c>_sccmCache</c>. Both fields live on the partial class so any
/// file in the partial set can read or write them.
/// </summary>
public partial class AppInventoryService
{
    // -----------------------------------------------------------------------
    // SCCM
    // -----------------------------------------------------------------------

    /// <summary>
    /// PowerShell preamble shared by every SCCM inventory script: import the
    /// ConfigurationManager module and land on the site drive, or stop with a
    /// message that says what to do.
    /// <para>
    /// The module creates a <c>SITE:</c> drive on import only when the
    /// Configuration Manager console has completed a connection to that site
    /// under the current Windows account (the MRU record then carries a site
    /// code). On a console-only workstation where the account has never run
    /// the console, no drive appears, and the old fallback
    /// (<c>New-PSDrive -Root '.'</c>) pointed at the local machine, which is
    /// only an SMS Provider on the site server itself. So this resolves the
    /// provider server the way the connectivity check does (console MRU, then
    /// the server the console installer recorded), builds the drive against
    /// it, and only then falls back to the local machine. Every failure is a
    /// terminating error with the site, the server tried and the remedy, so
    /// the inventory view shows that instead of "Cannot find drive".
    /// </para>
    /// </summary>
    internal static string SccmSitePreamble(string siteCode)
    {
        var escaped = siteCode.Replace("'", "''");
        return $@"
$adminPath = $env:SMS_ADMIN_UI_PATH
if (-not $adminPath) {{
    throw 'SMS_ADMIN_UI_PATH is not set. Install the Configuration Manager console, then restart Wrapp so it sees the variable.'
}}
$cmModule = Join-Path (Split-Path $adminPath -Parent) 'ConfigurationManager.psd1'
if (-not (Test-Path $cmModule)) {{
    throw ""ConfigurationManager module not found at $cmModule. Repair the Configuration Manager console installation.""
}}
Import-Module $cmModule -ErrorAction Stop

$siteCode  = '{escaped}'
$siteDrive = ""${{siteCode}}:""
if (-not (Get-PSDrive -Name $siteCode -PSProvider CMSite -ErrorAction SilentlyContinue)) {{
    $server = ''
    $mru = Get-ChildItem 'HKCU:\Software\Microsoft\ConfigMgr10\AdminUI\MRU' -ErrorAction SilentlyContinue | Sort-Object Name
    foreach ($k in $mru) {{
        $sn = (Get-ItemProperty $k.PSPath -Name 'ServerName' -ErrorAction SilentlyContinue).ServerName
        if ($sn) {{ $server = $sn; break }}
    }}
    if (-not $server) {{
        foreach ($key in 'HKLM:\SOFTWARE\Microsoft\ConfigMgr10\AdminUI\Connection', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\ConfigMgr10\AdminUI\Connection') {{
            $sn = (Get-ItemProperty $key -Name 'Server' -ErrorAction SilentlyContinue).Server
            if ($sn) {{ $server = $sn; break }}
        }}
    }}
    if (-not $server) {{ $server = '.' }}
    try {{
        New-PSDrive -Name $siteCode -PSProvider CMSite -Root $server -ErrorAction Stop | Out-Null
    }} catch {{
        $where = if ($server -eq '.') {{ 'this machine (no console connection is recorded for this Windows account)' }} else {{ ""'$server'"" }}
        throw ""Could not connect to ConfigMgr site $siteCode through $where - $($_.Exception.Message) Open the Configuration Manager console as this Windows account, connect to site $siteCode once, and check that the account is an administrative user in that site.""
    }}
}}
# Push-Location is -ErrorAction Stop: nothing below may run from the wrong drive.
Push-Location $siteDrive -ErrorAction Stop
";
    }

    public async Task<List<SCCMAppSummary>> GetSccmAppsAsync(string siteCode, bool forceRefresh = false)
    {
        if (!forceRefresh && _sccmCache.TryGetValue(siteCode, out var cached))
        {
            return cached.Apps;
        }

        using var op = OperationScope.Begin("Inventory.SccmRefresh");

        var script = SccmSitePreamble(siteCode) + @"
try {
    $apps = Get-CMApplication -Fast -ErrorAction Stop
    foreach ($app in $apps) {
        [PSCustomObject]@{
            CI_ID           = $app.CI_ID
            Name            = $app.LocalizedDisplayName
            Manufacturer    = $app.Manufacturer
            SoftwareVersion = $app.SoftwareVersion
            DeploymentCount = $app.NumberOfDeployments
            DependencyCount = $app.NumberOfDependentDTs
            IsDeployed      = $app.IsDeployed
        }
    }
} finally {
    Pop-Location
}
";

        var (results, errors) = await _ps.RunScriptWithErrorsAsync(script);

        foreach (var err in errors)
            AppLogger.Warn($"Inventory: SCCM app list error for site {siteCode}: {err}");

        var apps = new List<SCCMAppSummary>();

        foreach (var obj in results)
        {
            if (obj?.BaseObject is null) continue;
            apps.Add(new SCCMAppSummary
            {
                CI_ID           = GetStr(obj, "CI_ID"),
                Name            = GetStr(obj, "Name"),
                Manufacturer    = GetStr(obj, "Manufacturer"),
                SoftwareVersion = GetStr(obj, "SoftwareVersion"),
                SiteCode        = siteCode,
                DeploymentCount = GetInt(obj, "DeploymentCount"),
                DependencyCount = GetInt(obj, "DependencyCount"),
                IsDeployed      = GetBool(obj, "IsDeployed"),
            });
        }

        // Zero results + errors on the stream = the load FAILED (site
        // unreachable, drive creation refused, cmdlet error) -- not an empty
        // site. Throw so the caller reports failure instead of rendering an
        // empty list under a stuck status. Nothing is cached in this case.
        if (apps.Count == 0 && errors.Count > 0)
            throw new InvalidOperationException($"SCCM site '{siteCode}' query failed: {errors[0]}");

        _sccmCache[siteCode] = (SystemClock.UtcNow, apps);
        op.Complete($"loaded {apps.Count} SCCM app(s) for site {siteCode}");
        return apps;
    }

    public async Task<AppInventoryDetail?> GetSccmAppDetailAsync(string siteCode, string appName)
    {
        var cacheKey = $"sccm:{siteCode}:{appName}";
        if (_detailCache.TryGetValue(cacheKey, out var cached))
        {
            return cached.Detail;
        }

        var script = SccmSitePreamble(siteCode) + $@"
try {{
$app = Get-CMApplication -Name '{Escape(appName)}' -ErrorAction Stop
$dt = Get-CMDeploymentType -ApplicationName '{Escape(appName)}' -ErrorAction SilentlyContinue | Select-Object -First 1
$deployments = Get-CMApplicationDeployment -Name '{Escape(appName)}' -ErrorAction SilentlyContinue

$installCmd = ''; $uninstallCmd = ''; $repairCmd = ''; $contentLocation = ''
$dtName = ''; $technology = ''; $maxExecTime = 0; $installBehavior = ''
$detType = 'Unknown'; $detSummary = ''; $detDebug = ''

if ($dt) {{
    $dtName = $dt.LocalizedDisplayName
    $technology = if ($dt.Technology) {{ $dt.Technology }} else {{ '' }}
    try {{
        $xml = [xml]$dt.SDMPackageXML
        $installer = $xml.AppMgmtDigest.DeploymentType.Installer
        $detDebug += 'XML parsed OK. '
        $installCmd = ($installer.InstallAction.Args.Arg | Where-Object {{ $_.Name -eq 'InstallCommandLine' }}).'#text'
        $uninstallCmd = ($installer.UninstallAction.Args.Arg | Where-Object {{ $_.Name -eq 'InstallCommandLine' }}).'#text'
        $repairCmd = ($installer.RepairAction.Args.Arg | Where-Object {{ $_.Name -eq 'InstallCommandLine' }}).'#text'
        $contentLocation = $installer.Contents.Content.Location
        $maxExecTime = if ($installer.InstallAction.Args.Arg | Where-Object {{ $_.Name -eq 'MaxExecuteTime' }}) {{
            ($installer.InstallAction.Args.Arg | Where-Object {{ $_.Name -eq 'MaxExecuteTime' }}).'#text'
        }} else {{ 0 }}
        $installBehavior = if ($installer.InstallAction.Args.Arg | Where-Object {{ $_.Name -eq 'ExecutionContext' }}) {{
            ($installer.InstallAction.Args.Arg | Where-Object {{ $_.Name -eq 'ExecutionContext' }}).'#text'
        }} else {{ '' }}

        # Detection -- the authoritative source is DetectAction.Args:
        #   DetectionMethod = 'CustomScript' -> ScriptType/ScriptBody args hold the literal script
        #   DetectionMethod = 'Enhanced'     -> MethodBody arg holds EnhancedDetectionMethod XML
        #                                       (Settings children: File/Folder/RegistryKey/
        #                                        SimpleSetting/MSISettingInstance/ScriptSetting)
        #   DetectionMethod = 'ProductCode'  -> MSI product-code detection
        # Console/legacy variants can store the same data under Installer/CustomData
        # (DetectionScript / EnhancedDetectionMethod), so those are checked as fallbacks.
        $detScript = ''
        $detectAction = $xml.AppMgmtDigest.DeploymentType.Installer.DetectAction
        $detArgs = @()
        if ($detectAction -and $detectAction.Args) {{ $detArgs = @($detectAction.Args.Arg) }}
        $argNames = ($detArgs | ForEach-Object {{ $_.Name }}) -join ','
        $detDebug += 'DetectAction args: [' + $argNames + ']. '
        $methodArg = ($detArgs | Where-Object {{ $_.Name -eq 'DetectionMethod' }} | Select-Object -First 1).'#text'
        if ($methodArg) {{ $detDebug += 'DetectionMethod: ' + $methodArg + '. ' }}

        # 1) Custom script detection (script text lives in the ScriptBody arg)
        $scriptBodyArg = ($detArgs | Where-Object {{ $_.Name -eq 'ScriptBody' }} | Select-Object -First 1).'#text'
        $scriptTypeArg = ($detArgs | Where-Object {{ $_.Name -eq 'ScriptType' }} | Select-Object -First 1).'#text'
        if ($scriptBodyArg) {{
            $detType = 'Script'
            $detScript = $scriptBodyArg
            $lang = if ($scriptTypeArg) {{ $scriptTypeArg }} else {{ 'Script' }}
            $detSummary = $lang + ' script (' + $scriptBodyArg.Length + ' chars)'
            $detDebug += 'Script from DetectAction.ScriptBody. '
        }}
        if ($detType -eq 'Unknown') {{
            $customScript = $xml.AppMgmtDigest.DeploymentType.Installer.CustomData.DetectionScript
            if ($customScript -and $customScript.InnerText) {{
                $detType = 'Script'
                $detScript = $customScript.InnerText
                $lang = if ($customScript.Language) {{ $customScript.Language }} else {{ 'Script' }}
                $detSummary = $lang + ' script (' + $detScript.Length + ' chars)'
                $detDebug += 'Script from CustomData.DetectionScript. '
            }}
        }}

        # 2) MSI product-code detection
        if ($detType -eq 'Unknown' -and $methodArg -eq 'ProductCode') {{
            $pcArg = ($detArgs | Where-Object {{ $_.Name -eq 'ProductCode' }} | Select-Object -First 1).'#text'
            $detType = 'MSI Product Code'
            $detSummary = if ($pcArg) {{ 'Product: ' + $pcArg }} else {{ 'MSI product code detection' }}
            $detDebug += 'ProductCode method. '
        }}

        # 3) Enhanced (rule-based) detection: MethodBody XML string, or the same
        #    element tree under CustomData. Parsed namespace-agnostically.
        if ($detType -eq 'Unknown') {{
            $edm = $null
            $methodXml = ($detArgs | Where-Object {{ $_.Name -eq 'MethodBody' }} | Select-Object -First 1).'#text'
            if ($methodXml) {{
                $detDebug += 'MethodBody length: ' + $methodXml.Length + '. '
                try {{ $edm = ([xml]$methodXml).DocumentElement }}
                catch {{ $detDebug += 'MethodBody parse error: ' + $_.Exception.Message + '. ' }}
            }}
            if (-not $edm) {{
                $cd = $xml.AppMgmtDigest.DeploymentType.Installer.CustomData
                if ($cd -is [System.Xml.XmlElement]) {{
                    $cdNode = $cd.SelectSingleNode('*[local-name()=""EnhancedDetectionMethod""]')
                    if ($cdNode) {{ $edm = $cdNode; $detDebug += 'EnhancedDetectionMethod from CustomData. ' }}
                }}
            }}
            if ($edm) {{
                $settingNodes = $edm.SelectNodes('.//*[local-name()=""Settings""]/*')
                $detDebug += 'Settings children: [' + (($settingNodes | ForEach-Object {{ $_.LocalName }}) -join ',') + ']. '
                $ruleLines = @()
                $kinds = @()
                foreach ($s in $settingNodes) {{
                    switch ($s.LocalName) {{
                        'File'        {{ $kinds += 'File'; $ruleLines += 'File: ' + $s.Path + '\' + $s.Filter }}
                        'Folder'      {{ $kinds += 'File'; $ruleLines += 'Folder: ' + $s.Path + '\' + $s.Filter }}
                        'RegistryKey' {{ $kinds += 'Registry'; $ruleLines += 'Registry key: ' + $s.Hive + '\' + $s.Key }}
                        'SimpleSetting' {{
                            if ($s.RegistryDiscoverySource) {{
                                $rk = $s.RegistryDiscoverySource
                                $kinds += 'Registry'
                                $ruleLines += 'Registry: ' + $rk.Hive + '\' + $rk.Key + ' [' + $rk.ValueName + ']'
                            }} elseif ($s.FileSystemDiscoverySource) {{
                                $fs = $s.FileSystemDiscoverySource
                                $kinds += 'File'
                                $ruleLines += 'File: ' + $fs.Path + '\' + $fs.Filter
                            }} else {{
                                $kinds += 'Enhanced'
                                $ruleLines += 'Setting: ' + $s.LogicalName
                            }}
                        }}
                        'MSISettingInstance' {{ $kinds += 'MSI Product Code'; $ruleLines += 'MSI product code: ' + $s.ProductCode }}
                        'ScriptSetting' {{
                            $kinds += 'Script'
                            $body = $s.SelectSingleNode('.//*[local-name()=""ScriptBody""]')
                            if (-not $body) {{ $body = $s.SelectSingleNode('.//*[local-name()=""DiscoveryScriptBody""]') }}
                            if ($body -and $body.InnerText) {{
                                $detScript = $body.InnerText
                                $ruleLines += 'Script (' + $detScript.Length + ' chars)'
                            }} else {{ $ruleLines += 'Script setting' }}
                        }}
                        default {{ $kinds += 'Enhanced'; $ruleLines += $s.LocalName }}
                    }}
                }}
                if ($ruleLines.Count -gt 0) {{
                    $uniqueKinds = @($kinds | Select-Object -Unique)
                    $detType = if ($ruleLines.Count -eq 1) {{ $uniqueKinds[0] }}
                               elseif ($uniqueKinds.Count -eq 1) {{ $uniqueKinds[0] + ' (' + $ruleLines.Count + ' rules)' }}
                               else {{ 'Rules (' + $ruleLines.Count + ')' }}
                    $detSummary = $ruleLines -join '; '
                    $detDebug += 'Parsed ' + $ruleLines.Count + ' rule(s). '
                }} else {{
                    $detType = 'Enhanced'
                    $detSummary = 'Enhanced detection method'
                    $detDebug += 'EnhancedDetectionMethod present but no Settings children. '
                }}
            }} else {{
                $detDebug += 'No detection source found. '
            }}
        }}

        # Content size from the XML
        $contentSize = 0
        try {{
            $contentNode = $installer.Contents.Content
            if ($contentNode.ContentSize) {{ $contentSize = [long]$contentNode.ContentSize }}
        }} catch {{}}
    }} catch {{}}
}}

# Categories
$categories = @()
if ($app.LocalizedCategoryInstanceNames) {{ $categories = @($app.LocalizedCategoryInstanceNames) }}

# Parse dependencies and supersedence from app-level SDMPackageXML
$depsRaw = @()
$supsRaw = @()
try {{
    $appXml = [xml]$app.SDMPackageXML
    # Dependencies are in DeploymentType/Dependencies
    $dtNodes = $appXml.AppMgmtDigest.DeploymentType
    if ($dtNodes) {{
        foreach ($dtNode in @($dtNodes)) {{
            $depRules = $dtNode.Dependencies.DeploymentTypeRule
            foreach ($rule in @($depRules)) {{
                $intent = $rule.DeploymentTypeExpression.Operands.DeploymentTypeIntentExpression
                if ($intent.DeploymentTypeApplicationReference) {{
                    $ref = $intent.DeploymentTypeApplicationReference
                    $depName = if ($ref.AuthoringScopeId) {{ $ref.LogicalName }} else {{ 'Unknown' }}
                    $depsRaw += [PSCustomObject]@{{ AppId = ''; AppName = $depName; Type = 'Dependency'; AutoInstall = $true }}
                }}
            }}
        }}
    }}
    # Supersedence
    $supersedes = $appXml.AppMgmtDigest.Application.Supersedes
    if ($supersedes) {{
        foreach ($sup in @($supersedes.DeploymentTypeRule)) {{
            $supRef = $sup.DeploymentTypeExpression.Operands.DeploymentTypeIntentExpression.DeploymentTypeApplicationReference
            if ($supRef) {{
                $supsRaw += [PSCustomObject]@{{ AppId = ''; AppName = if ($supRef.LogicalName) {{ $supRef.LogicalName }} else {{ 'Unknown' }}; Type = 'replace' }}
            }}
        }}
    }}
}} catch {{}}

[PSCustomObject]@{{
    Platform           = 'SCCM'
    Id                 = $app.CI_ID.ToString()
    DisplayName        = $app.LocalizedDisplayName
    Publisher          = $app.Manufacturer
    Version            = $app.SoftwareVersion
    Description        = $app.LocalizedDescription
    CreatedDateTime    = if ($app.DateCreated) {{ $app.DateCreated.ToString('o') }} else {{ '' }}
    LastModifiedDateTime = if ($app.DateLastModified) {{ $app.DateLastModified.ToString('o') }} else {{ '' }}
    InstallCommand     = if ($installCmd) {{ $installCmd }} else {{ '' }}
    UninstallCommand   = if ($uninstallCmd) {{ $uninstallCmd }} else {{ '' }}
    RepairCommand      = if ($repairCmd) {{ $repairCmd }} else {{ '' }}
    ContentLocation    = if ($contentLocation) {{ $contentLocation }} else {{ '' }}
    DeploymentTypeName = $dtName
    InstallExperience  = $installBehavior
    MaxInstallTime     = $maxExecTime
    DetectionType      = $detType
    DetectionSummary   = $detSummary
    DetectionScript    = if ($detScript) {{ $detScript }} else {{ '' }}
    DetectionDebug     = $detDebug
    SizeInBytes        = $contentSize
    FileName           = if ($technology) {{ $technology + ' deployment type' }} else {{ '' }}
    Technology         = if ($technology) {{ $technology }} else {{ '' }}
    IsEnabled          = [bool]$app.IsEnabled
    IsExpired          = [bool]$app.IsExpired
    IsSuperseded       = [bool]$app.IsSuperseded
    CreatedBy          = if ($app.CreatedBy) {{ $app.CreatedBy }} else {{ '' }}
    LastModifiedBy     = if ($app.LastModifiedBy) {{ $app.LastModifiedBy }} else {{ '' }}
    NumberOfDeploymentTypes = if ($app.NumberOfDeploymentTypes) {{ $app.NumberOfDeploymentTypes }} else {{ 0 }}
    EstimatedInstallTime = 0
    ObjectPath         = if ($app.ObjectPath) {{ $app.ObjectPath }} else {{ '' }}
    Categories         = $categories
    Assignments        = @($deployments | ForEach-Object {{
        $intent = switch ($_.DesiredConfigType) {{ 1 {{ 'Required' }} 2 {{ 'Available' }} default {{ $_.DesiredConfigType.ToString() }} }}
        [PSCustomObject]@{{
            Intent     = $intent
            TargetType = 'Collection'
            TargetLabel = $_.CollectionName
            GroupId    = $_.CollectionID
            GroupMode  = 'Include'
            Notification = if ($_.UserUIExperience) {{ 'Show' }} else {{ 'Hide' }}
            AvailableTime = if ($_.StartTime) {{ $_.StartTime.ToString('o') }} else {{ '' }}
            DeadlineTime = if ($_.EnforcementDeadline) {{ $_.EnforcementDeadline.ToString('o') }} else {{ '' }}
            DeliveryOptimization = ''
            RestartGracePeriod = ''
            FilterId   = ''
            FilterMode = ''
            Source      = 'direct'
        }}
    }})
    Dependencies       = $depsRaw
    Supersedence       = $supsRaw
    Requirements       = @()
}}
}} finally {{ Pop-Location }}
";

        var (results, detailErrors) = await _ps.RunScriptWithErrorsAsync(script);
        foreach (var err in detailErrors)
            AppLogger.Warn($"Inventory: SCCM detail error for '{appName}': {err}");
        if (results.Count == 0) return null;

        var detDebug = GetStr(results[0], "DetectionDebug");
        if (!string.IsNullOrEmpty(detDebug))
            AppLogger.Info($"[SCCM Detection Debug] {appName}: {detDebug}");
        var detType = GetStr(results[0], "DetectionType");
        var detSummary = GetStr(results[0], "DetectionSummary");
        var detScript = GetStr(results[0], "DetectionScript");
        AppLogger.Info($"[SCCM Detection Result] {appName}: Type={detType}, Summary={detSummary}, ScriptLength={detScript?.Length ?? 0}");

        var detail = MapDetail(results[0]);
        _detailCache[cacheKey] = (SystemClock.UtcNow, detail);
        // Also cache by CI_ID so search can find it
        if (!string.IsNullOrEmpty(detail.Id))
            _detailCache[detail.Id] = (SystemClock.UtcNow, detail);
        return detail;
    }

}
