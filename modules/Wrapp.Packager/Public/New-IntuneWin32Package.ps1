function New-IntuneWin32Package {
    <#
    .SYNOPSIS
        Creates an .intunewin package from source files and returns package metadata.

    .PARAMETER SourcePath
        Path to the application source folder.

    .PARAMETER SetupFile
        Relative path to the setup/install script within the source.

    .PARAMETER OutputPath
        Directory for the output .intunewin file.

    .PARAMETER PackageName
        Desired filename for the .intunewin package (e.g., "Chrome_Enterprise_139.intunewin").

    .PARAMETER Validate
        If specified, outputs what would happen without creating the package.

    .OUTPUTS
        [PSCustomObject] with: Path, Name, SizeBytes, SizeFormatted, Modified
        Returns $null in Validate mode.

        B' (stage 3): also emits Write-WrappStep 'Wrapping' Progress broadcast
        events (Package = '*') interleaved on the output stream. Callers must
        consume this function as a stream and separate WrappStep events from
        the final package object (see Invoke-WrappIntune Phase 10).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$SetupFile,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath,

        [Parameter(Mandatory = $true)]
        [string]$PackageName,

        [switch]$Validate
    )

    # Ensure output directory exists
    if (-not (Test-Path -Path $OutputPath)) {
        New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
        Write-Log "Created output directory: $OutputPath"
    }

    Write-Log "Creating package '$PackageName'"
    Write-Log "  Source: $SourcePath"
    Write-Log "  Setup file: $SetupFile"
    Write-Log "  Output: $OutputPath"
    Write-WrappStep -Package '*' -Step 'Wrapping' -Kind 'Progress' -Detail 'Preparing...' -Percent 0

    if ($Validate) {
        $PackageParams = @{
            SourceFolder = $SourcePath
            SetupFile    = $SetupFile
            OutputFolder = $OutputPath
            Force        = $true
        }
        Write-Log "[VALIDATE] Package creation skipped. Parameters:"
        foreach ($key in $PackageParams.Keys) {
            Write-Log "  $key = $($PackageParams[$key])"
        }
        return $null
    }

    # IntuneWinAppUtil.exe wraps everything in the source folder with no
    # exclusion support. Copy to a temp folder minus unwanted directories
    # so items like .git (plus Wrapp's .git.backup-* archives from the
    # 0.6.0.0156 Script/ migration) don't bloat the .intunewin package.
    $ExcludeDirs = @('.git', '.svn', '.hg', 'Output', '.vs', 'node_modules', '.git.backup-*')
    $HasExcluded = $false
    foreach ($dir in $ExcludeDirs) {
        if (Test-Path -Path (Join-Path -Path $SourcePath -ChildPath $dir)) {
            $HasExcluded = $true
            break
        }
    }

    $EffectiveSource = $SourcePath
    $TempSourcePath  = $null
    if ($HasExcluded) {
        $TempSourcePath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath "IntuneWrap_$([guid]::NewGuid().ToString('N').Substring(0,8))"
        Write-Log "Copying source files to staging area..."
        Write-Log "  Temp: $TempSourcePath"
        Write-WrappStep -Package '*' -Step 'Wrapping' -Kind 'Progress' -Detail 'Copying source files...' -Percent 20

        # robocopy mirrors the source, excluding the unwanted directories.
        # SECURITY: invoke robocopy directly with an argument array (`& robocopy
        # @robocopyArgs`) instead of building a command string for `cmd /c`.
        # cmd's parser is independent of PowerShell's, so a $SourcePath like
        # `C:\pkg" & calc.exe & "` would break out of the quotes and execute
        # arbitrary commands. Array splatting passes each element as a single
        # Win32 argument with no shell re-parsing -- no metacharacter injection.
        $robocopyArgs = @($SourcePath, $TempSourcePath, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
        foreach ($dir in $ExcludeDirs) {
            $robocopyArgs += '/XD'
            $robocopyArgs += $dir
        }
        Write-Log "  robocopy $($robocopyArgs -join ' ')"
        & robocopy @robocopyArgs | Out-Null
        # robocopy exit codes 0-7 are success
        if ($LASTEXITCODE -gt 7) {
            throw "robocopy failed with exit code $LASTEXITCODE"
        }

        $excluded = $ExcludeDirs | Where-Object { Test-Path -Path (Join-Path -Path $SourcePath -ChildPath $_) }
        Write-Log "Source files copied to staging area"
        Write-Log "  Excluded: $($excluded -join ', ')"
        Write-WrappStep -Package '*' -Step 'Wrapping' -Kind 'Progress' -Detail 'Source files staged' -Percent 50
        $EffectiveSource = $TempSourcePath
    }

    $PackageParams = @{
        SourceFolder = $EffectiveSource
        SetupFile    = $SetupFile
        OutputFolder = $OutputPath
        Force        = $true
    }

    try {
        # Create the .intunewin package
        Write-Log "Running IntuneWinAppUtil to create .intunewin package..."
        Write-WrappStep -Package '*' -Step 'Wrapping' -Kind 'Progress' -Detail 'Creating .intunewin...' -Percent 60
        $IntuneWinPackage = New-IntuneWin32AppPackage @PackageParams
    }
    finally {
        # Clean up temp copy
        if ($TempSourcePath -and (Test-Path -Path $TempSourcePath)) {
            Remove-Item -Path $TempSourcePath -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log "Cleaned up temp source: $TempSourcePath"
        }
    }

    # Validate that the package was created successfully
    if (-not $IntuneWinPackage -or -not $IntuneWinPackage.Path) {
        throw "IntuneWin32AppPackage creation failed - no output package returned. Check IntuneWin32App module output above for details."
    }

    # Rename to desired name
    $FinalPath = Join-Path -Path (Split-Path -Path $IntuneWinPackage.Path -Parent) -ChildPath $PackageName
    if (Test-Path -Path $FinalPath) {
        Remove-Item -Path $FinalPath -Force
    }
    Rename-Item -Path $IntuneWinPackage.Path -NewName $PackageName

    # Verify and build summary
    $PackageFile = Get-Item -Path $FinalPath
    if (-not $PackageFile) {
        throw "Package file not found after creation: $FinalPath"
    }

    $Result = [PSCustomObject]@{
        Path          = $PackageFile.FullName
        Name          = $PackageFile.Name
        SizeBytes     = $PackageFile.Length
        SizeFormatted = Format-FileSize -Bytes $PackageFile.Length
        Modified      = $PackageFile.LastWriteTime
    }

    Write-Log "Package created: $($Result.Name) ($($Result.SizeFormatted))"
    Write-WrappStep -Package '*' -Step 'Wrapping' -Kind 'Progress' -Detail 'Package created' -Percent 100
    return $Result
}
