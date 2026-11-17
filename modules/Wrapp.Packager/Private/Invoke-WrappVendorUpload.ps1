function Invoke-WrappVendorUpload {
    <#
    .SYNOPSIS
        Runs one IntuneWin32App call that uploads a .intunewin, keeping
        everything it reports and cleaning up what it leaves behind.

    .DESCRIPTION
        The vendored IntuneWin32App cmdlets report failures as warnings or
        non-terminating errors and then return nothing, so a caller that only
        looks at the result learns "no result" and nothing else. This helper
        merges the verbose, warning and error streams of the call:

          - verbose lines are re-emitted (the run log keeps the raw vendor
            diagnostics) and translated into AppCreation progress steps;
          - warnings and errors are written to the run log and collected in
            $State.Problems, so the caller can say WHY the upload failed;
          - anything else is the call's result, kept in $State.Result.

        The vendor unpacks the upload file into an Expand_<12 hex> folder
        beside the .intunewin and deletes it only on the success path. Any
        such folder that appears during this call is removed afterwards,
        whatever the outcome; folders that existed before are left alone.

        Step events (Write-WrappStep objects) flow to this function's output,
        so callers consume it as a stream exactly as before.

    .PARAMETER Call
        The vendor call, e.g. { Add-IntuneWin32App @Win32AppArgs -Verbose }.

    .PARAMETER IntuneWinPath
        The .intunewin being uploaded; its folder is where Expand_ appears.

    .PARAMETER State
        A hashtable this function fills: Result, Problems (string[]),
        RemovedFolders (string[]).

    .PARAMETER AppName
        For step events and log lines.

    .PARAMETER TenantID
        For step events.

    .PARAMETER Translate
        Translate vendor verbose lines into AppCreation progress steps.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Call,

        [Parameter(Mandatory = $true)]
        [string]$IntuneWinPath,

        [Parameter(Mandatory = $true)]
        [hashtable]$State,

        [string]$AppName = '',

        [string]$TenantID = '',

        [switch]$Translate
    )

    $State['Result'] = $null
    $State['AppId'] = $null
    $Problems = [System.Collections.Generic.List[string]]::new()
    $Removed = [System.Collections.Generic.List[string]]::new()
    $Kept = [System.Collections.Generic.List[string]]::new()

    $OutputDir = [System.IO.Path]::GetDirectoryName($IntuneWinPath)
    $ExpandPattern = '^Expand_[0-9a-fA-F]{12}$'
    $Before = @()
    if ($OutputDir -and (Test-Path -LiteralPath $OutputDir -PathType Container)) {
        $Before = @(Get-ChildItem -LiteralPath $OutputDir -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match $ExpandPattern } | ForEach-Object { $_.FullName })
    }

    try {
        & $Call 4>&1 3>&1 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.VerboseRecord]) {
                Write-Verbose -Message $_.Message -Verbose
                # The ID of the app the vendor just registered, from its own
                # report of Intune's response: the app THIS call created.
                if ($_.Message -match 'Successfully created Win32 app with ID: (\S+)') {
                    $State['AppId'] = $Matches[1].Trim()
                }
                if ($Translate) {
                    $SubStep = Convert-VendorVerboseToStep -Message $_.Message
                    if ($SubStep) {
                        $StepParams = @{
                            Package  = $AppName
                            Step     = 'AppCreation'
                            Kind     = 'Progress'
                            TenantId = $TenantID
                            Detail   = if ($SubStep.Detail) { "$($SubStep.Status)|$($SubStep.Detail)" } else { $SubStep.Status }
                            Percent  = $SubStep.Percent
                        }
                        Write-WrappStep @StepParams
                    }
                }
            }
            elseif ($_ -is [System.Management.Automation.WarningRecord]) {
                $Problems.Add($_.Message)
                Write-Log "IntuneWin32App: $($_.Message)" -Type 2
            }
            elseif ($_ -is [System.Management.Automation.ErrorRecord]) {
                $Message = if ($_.Exception) { $_.Exception.Message } else { "$_" }
                $Problems.Add($Message)
                Write-Log "IntuneWin32App error: $Message" -Type 3
            }
            elseif ($null -ne $_) {
                $State['Result'] = $_
            }
        }
    }
    catch {
        $Problems.Add("$_")
        Write-Log "IntuneWin32App call failed: $_" -Type 3
    }
    finally {
        if ($OutputDir -and (Test-Path -LiteralPath $OutputDir -PathType Container)) {
            $Leftovers = @(Get-ChildItem -LiteralPath $OutputDir -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match $ExpandPattern -and $Before -notcontains $_.FullName })
            foreach ($Folder in $Leftovers) {
                try {
                    Remove-Item -LiteralPath $Folder.FullName -Recurse -Force -ErrorAction Stop
                    $Removed.Add($Folder.FullName)
                    Write-Log "Removed the upload's leftover extraction folder: $($Folder.FullName)"
                }
                catch {
                    $Kept.Add($Folder.FullName)
                    Write-Log "Could not remove the upload's extraction folder $($Folder.FullName): $_" -Type 2
                }
            }
        }
        $State['Problems'] = $Problems.ToArray()
        $State['RemovedFolders'] = $Removed.ToArray()
        $State['LeftoverFolders'] = $Kept.ToArray()
    }
}
