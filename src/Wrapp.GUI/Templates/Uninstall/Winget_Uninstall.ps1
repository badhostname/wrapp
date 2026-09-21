# Winget Uninstall - Removes {{Name}} via Windows Package Manager
$ErrorActionPreference = 'Stop'

$AppId = "{{Name}}" # Replace with the actual winget package ID

$WingetPath = Get-Command winget -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $WingetPath) {
    $WingetPath = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
}

if (-not (Test-Path $WingetPath)) {
    Write-Error "winget not found on this system"
    exit 1
}

$Process = Start-Process -FilePath $WingetPath -ArgumentList @(
    'uninstall'
    '--id', $AppId
    '--silent'
    '--accept-source-agreements'
) -Wait -PassThru -NoNewWindow

exit $Process.ExitCode
