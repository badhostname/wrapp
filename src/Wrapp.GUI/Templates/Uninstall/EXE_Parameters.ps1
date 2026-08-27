# EXE Uninstall - Finds and executes the uninstall string from registry
$ErrorActionPreference = 'Stop'

$AppName = "{{Name}}"

$UninstallPaths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)

$Entry = foreach ($Path in $UninstallPaths) {
    Get-ItemProperty -Path $Path -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "$AppName*" }
} | Select-Object -First 1

if (-not $Entry) {
    Write-Host "Application '$AppName' not found in registry - may already be uninstalled"
    exit 0
}

$UninstallString = $Entry.QuietUninstallString
if (-not $UninstallString) { $UninstallString = $Entry.UninstallString }

if ($UninstallString) {
    Write-Host "Uninstalling: $($Entry.DisplayName)"
    # Append silent switch if not already present
    if ($UninstallString -notmatch '/S|/silent|/quiet|/qn') {
        $UninstallString += ' /S'
    }
    $Process = Start-Process cmd.exe -ArgumentList "/c `"$UninstallString`"" -Wait -PassThru
    exit $Process.ExitCode
}

Write-Error "No uninstall string found for '$AppName'"
exit 1
