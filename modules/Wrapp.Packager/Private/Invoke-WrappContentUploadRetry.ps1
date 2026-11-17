function Invoke-WrappContentUploadRetry {
    <#
    .SYNOPSIS
        Re-uploads the .intunewin onto the Intune app this run just created,
        when that app's first upload failed.

    .DESCRIPTION
        Add-IntuneWin32App registers the app first and uploads its content
        second, and it cannot be called twice without creating a second app.
        When the upload part fails, this retries ONLY the content, onto the
        same app, through the vendor's Update-IntuneWin32AppPackageFile (new
        content version, new storage URI, upload, commit) - the vendor's own
        polling and chunk retries stay as they are.

        Before every attempt the app is read back by the ID the vendor
        reported when it created it, and the retry goes ahead only when:
          - the app still exists, and its display name is the one this run
            created (so a deleted or reused ID is never touched);
          - it has no committed content (committedContentVersion is empty).
            If content did land after all, the app is returned as a success.

        Attempts and waits come from Defaults.psd1 (ContentUploadRetryCount,
        ContentUploadRetryDelaySeconds x attempt number). Each attempt runs
        through Invoke-WrappVendorUpload, so its reasons are kept and its
        Expand_ folder removed.

    .PARAMETER AppId
        The ID Add-IntuneWin32App reported for the app it created.

    .PARAMETER AppName
        The display name the run gave that app.

    .PARAMETER IntuneWinPath
        The .intunewin to upload.

    .PARAMETER State
        Filled with: Result (the app with content, or $null), Problems
        (string[] across attempts), Attempts (uploads tried, the first one
        included), LeftoverFolders, Stopped (why retrying stopped early).

    .OUTPUTS
        WrappStep progress events, as a stream.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$AppId,
        [Parameter(Mandatory = $true)][string]$AppName,
        [Parameter(Mandatory = $true)][string]$IntuneWinPath,
        [Parameter(Mandatory = $true)][string]$TenantID,
        [Parameter(Mandatory = $true)][string]$ClientID,
        [Parameter(Mandatory = $true)][hashtable]$State
    )

    # Read directly: Get-ConfigValue treats 0 as "not set", and 0 retries or
    # a 0 s wait are legitimate settings.
    $Retries = if ($null -ne $script:ModuleDefaults.ContentUploadRetryCount) { [int]$script:ModuleDefaults.ContentUploadRetryCount } else { 2 }
    $Delay   = if ($null -ne $script:ModuleDefaults.ContentUploadRetryDelaySeconds) { [int]$script:ModuleDefaults.ContentUploadRetryDelaySeconds } else { 30 }
    $Problems = [System.Collections.Generic.List[string]]::new()
    $Leftovers = [System.Collections.Generic.List[string]]::new()
    $State['Result'] = $null
    $State['Attempts'] = 1
    $State['Stopped'] = $null

    for ($Attempt = 2; $Attempt -le $Retries + 1; $Attempt++) {
        # Is it still the app this run created, and still without content?
        try {
            $Existing = Get-IntuneWin32App -ID $AppId -ErrorAction Stop
        }
        catch {
            $Existing = $null
        }
        if (-not $Existing) {
            $State['Stopped'] = "the app '$AppId' could not be read back from Intune"
            break
        }
        if ("$($Existing.displayName)" -ne $AppName) {
            $State['Stopped'] = "the app '$AppId' is now named '$($Existing.displayName)', not '$AppName'"
            break
        }
        if (-not [string]::IsNullOrEmpty("$($Existing.committedContentVersion)")) {
            Write-Log "App '$AppName' ($AppId) has committed content after all - treating the upload as done."
            $State['Result'] = $Existing
            break
        }

        $Wait = $Delay * ($Attempt - 1)
        $Msg = "Upload attempt $Attempt of $($Retries + 1) onto the app this run created ($AppId), in $Wait s"
        Write-Log $Msg -Type 2
        Write-WrappStep -Package $AppName -Step 'AppCreation' -Kind 'Progress' -TenantId $TenantID -Detail "Upload retry $($Attempt - 1) of $Retries"
        if ($Wait -gt 0) { Start-Sleep -Seconds $Wait }

        Invoke-TokenRefreshIfNeeded -TenantID $TenantID -ClientID $ClientID
        $State['Attempts'] = $Attempt
        $Upload = @{}
        $UploadParams = @{
            Call          = { Update-IntuneWin32AppPackageFile -ID $AppId -FilePath $IntuneWinPath -Verbose }
            IntuneWinPath = $IntuneWinPath
            State         = $Upload
            AppName       = $AppName
            TenantID      = $TenantID
            Translate     = $true
        }
        Invoke-WrappVendorUpload @UploadParams
        foreach ($f in @($Upload.LeftoverFolders)) { if ($f) { $Leftovers.Add($f) } }

        if ($Upload.Problems) {
            foreach ($p in $Upload.Problems) { $Problems.Add("attempt ${Attempt}: $p") }
            continue
        }

        # The vendor reports success only by not complaining; confirm the commit.
        try { $After = Get-IntuneWin32App -ID $AppId -ErrorAction Stop } catch { $After = $null }
        if ($After -and -not [string]::IsNullOrEmpty("$($After.committedContentVersion)")) {
            Write-Log "Upload attempt $Attempt succeeded for '$AppName' ($AppId)."
            $State['Result'] = $After
            break
        }
        $Problems.Add("attempt ${Attempt}: the upload reported no error but the app still has no committed content")
    }

    $State['Problems'] = $Problems.ToArray()
    $State['LeftoverFolders'] = $Leftovers.ToArray()
}
