<#
.SYNOPSIS
    Wrapp.Packager module loader.

.DESCRIPTION
    Dot-sources all Public and Private function files, loads module defaults,
    and initializes module-scope state. Follows the same pattern as
    IntuneWin32App 1.5.0 (IntuneWin32App.psm1).
#>
[CmdletBinding()]
param()

Process {
    # Module root path
    $script:ModuleRoot = $PSScriptRoot

    # Load module defaults from data file
    $script:DefaultsPath = Join-Path -Path $PSScriptRoot -ChildPath 'Config\Defaults.psd1'
    if (Test-Path -Path $script:DefaultsPath) {
        $script:ModuleDefaults = Import-PowerShellDataFile -Path $script:DefaultsPath
    }
    else {
        Write-Warning "Wrapp.Packager: Defaults.psd1 not found at $($script:DefaultsPath)"
        $script:ModuleDefaults = @{}
    }

    # Module-scope log state (initialized per-session by Initialize-LogFile)
    $script:LogFile = $null

    # Phase 11 hardening (S-2): SCCM site code is recorded by
    # Connect-WrappSccm and consumed by Invoke-WrappSccm's finally
    # cleanup to dismount the PSDrive. Initialize null so a pooled runspace
    # without a prior connection doesn't try to remove a phantom drive.
    $script:SccmSiteCode = $null

    # Dot-source all function files
    $PrivateFunctions = @(Get-ChildItem -Path (Join-Path -Path $PSScriptRoot -ChildPath 'Private') -Filter '*.ps1' -ErrorAction SilentlyContinue)
    $PublicFunctions = @(Get-ChildItem -Path (Join-Path -Path $PSScriptRoot -ChildPath 'Public') -Filter '*.ps1' -ErrorAction SilentlyContinue)

    # Load private first (public functions may depend on them)
    foreach ($FunctionFile in $PrivateFunctions) {
        try {
            . $FunctionFile.FullName
        }
        catch [System.Exception] {
            Write-Error -Message "Failed to import function '$($FunctionFile.FullName)': $($_.Exception.Message)"
        }
    }

    foreach ($FunctionFile in $PublicFunctions) {
        try {
            . $FunctionFile.FullName
        }
        catch [System.Exception] {
            Write-Error -Message "Failed to import function '$($FunctionFile.FullName)': $($_.Exception.Message)"
        }
    }

    # Backwards-compatible aliases: the pre-Wrapp function names (IntunePackager /
    # SCCMPackager script era) still resolve for existing CLI scripts and docs.
    # New code should use the Wrapp-prefixed names.
    $LegacyAliases = @{
        'Invoke-IntunePackager'         = 'Invoke-WrappIntune'
        'Invoke-SCCMPackager'           = 'Invoke-WrappSccm'
        'Connect-IntunePackager'        = 'Connect-WrappIntune'
        'Connect-SCCMPackager'          = 'Connect-WrappSccm'
        'Test-PackagerConfig'           = 'Test-WrappConfig'
        'Test-IntunePackagerPreflight'  = 'Test-WrappIntunePreflight'
        'Test-SCCMPackagerPreflight'    = 'Test-WrappSccmPreflight'
    }
    foreach ($legacy in $LegacyAliases.GetEnumerator()) {
        Set-Alias -Name $legacy.Key -Value $legacy.Value
    }

    # Export only public functions (+ the legacy-name aliases)
    Export-ModuleMember -Function $PublicFunctions.BaseName -Alias @($LegacyAliases.Keys)
}
