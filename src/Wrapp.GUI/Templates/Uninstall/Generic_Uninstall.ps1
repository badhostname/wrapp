# Generic Uninstall - Searches registry for {{Name}} and executes the uninstall command
$ErrorActionPreference = 'Stop'

$AppName = "{{Name}}"

$UninstallPaths = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)

$Found = $false

foreach ($Path in $UninstallPaths) {
    Get-ItemProperty -Path $Path -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "$AppName*" } |
        ForEach-Object {
            $Found = $true
            $DisplayName = $_.DisplayName
            Write-Host "Found: $DisplayName"

            if ($_.UninstallString -match 'msiexec') {
                $ProductCode = $_.PSChildName
                Write-Host "Uninstalling MSI: $ProductCode"
                $Proc = Start-Process 'msiexec.exe' -ArgumentList "/x `"$ProductCode`" /qn /norestart" -Wait -PassThru
                if ($Proc.ExitCode -notin @(0, 1641, 3010)) {
                    Write-Warning "MSI uninstall returned: $($Proc.ExitCode)"
                }
            } else {
                $Cmd = $_.QuietUninstallString
                if (-not $Cmd) { $Cmd = $_.UninstallString }
                if ($Cmd) {
                    Write-Host "Executing: $Cmd"
                    Start-Process cmd.exe -ArgumentList "/c `"$Cmd /S`"" -Wait
                }
            }
        }
}

if (-not $Found) {
    Write-Host "'$AppName' not found in registry - may already be uninstalled"
}

exit 0
