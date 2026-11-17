function Initialize-LogFile {
    <#
    .SYNOPSIS
        Sets up the module-scope log file path and performs log rotation.

    .PARAMETER LogPath
        Full path to the log file. If directory does not exist, it will be created.

    .PARAMETER MaxSizeKB
        Maximum log file size in KB before rotation. Default: 5120 (5 MB).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [int]$MaxSizeKB = 5120
    )

    # Ensure directory exists BEFORE switching the log path.
    # This prevents $script:LogFile from pointing to an unreachable path
    # if the directory creation fails (e.g. network share offline).
    $LogDir = Split-Path -Path $LogPath -Parent
    if (-not (Test-Path -Path $LogDir)) {
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
    }

    # Set module-scope log file path (only after directory is confirmed)
    $script:LogFile = $LogPath

    # Rotation: rename existing log to .lo_ if over threshold
    if (Test-Path -Path $LogPath) {
        $FileInfo = [System.IO.FileInfo]$LogPath
        if ($FileInfo.Length -gt ($MaxSizeKB * 1KB)) {
            $BackupPath = Join-Path -Path $FileInfo.DirectoryName -ChildPath ($FileInfo.BaseName + '.lo_')
            if (Test-Path -Path $BackupPath) {
                try { Remove-Item -Path $BackupPath -Force } catch { }
            }
            try {
                Rename-Item -Path $LogPath -NewName ($FileInfo.BaseName + '.lo_') -Force
            }
            catch { }
        }
    }

    Write-Log "Log initialized: $LogPath"
    Write-Log "Wrapp.Packager module v$($script:ModuleDefaults.ScriptVersion) starting at $(Get-Date)"
}
