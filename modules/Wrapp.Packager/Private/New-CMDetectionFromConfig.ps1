function New-CMDetectionFromConfig {
    <#
    .SYNOPSIS
        Prepares DetectScript.ps1 for SCCM import by injecting Config.json data.

    .DESCRIPTION
        Loads the DetectScript.ps1 template and injects Config.App and
        Config.Script.Detect data as inline code. Optionally injects a
        $PackageOption variable for multi-package detection expression routing.
        Returns the prepared script content as a string for use with
        Add-CMScriptDeploymentType -ScriptText.

    .PARAMETER DetectScriptPath
        Full path to DetectScript.ps1 template.

    .PARAMETER App
        The Config.App object to inject.

    .PARAMETER DetectConfig
        The Config.Script.Detect object to inject.

    .PARAMETER PackageOption
        Optional package option string for detection expression routing.

    .OUTPUTS
        [string] The prepared detection script content with injected config data.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DetectScriptPath,

        [Parameter(Mandatory = $true)]
        $App,

        [Parameter(Mandatory = $true)]
        $DetectConfig,

        [string]$PackageOption
    )

    if (-not (Test-Path -Path "FileSystem::$DetectScriptPath")) {
        Write-Log "DetectScript.ps1 not found at: $DetectScriptPath" -Type 3
        return $null
    }

    $DetectScript = Get-Content -Raw -Path "FileSystem::$DetectScriptPath"

    # Expand the endpoint tag-folder token (see New-DetectionRuleFromConfig).
    if ($DetectScript.Contains('{{TagFolder}}')) {
        $DetectScript = $DetectScript.Replace('{{TagFolder}}', [string]$script:ModuleDefaults.EndpointTagFolder)
    }

    $ConfigString = ""

    if ($PackageOption) {
        Write-Log "Injecting PackageOption: $PackageOption"
        $ConfigString += "`$PackageOption = ""$PackageOption""`r`n`r`n"
    }

    # Convert Config.App into inline code
    $ConfigString += "`$Config = @{}`r`n"

    if ($App) {
        $AppJson = $App | ConvertTo-Json
        $ConfigString += @"
`$Config += @{
	App = '$AppJson' | ConvertFrom-Json
	}`r`n
"@
    }

    # Convert Config.Script.Detect into inline code
    if ($DetectConfig) {
        $DetectJson = $DetectConfig | ConvertTo-Json
        $ConfigString += @"
`$Config += @{
	Script = @{
	Detect = '$DetectJson' | ConvertFrom-Json
	}}`r`n
"@
    }

    $DetectScript = $DetectScript.Replace("# Inject Config.json data here", $ConfigString)

    Write-Log "Detection script prepared ($($DetectScript.Length) characters)"

    return $DetectScript
}
