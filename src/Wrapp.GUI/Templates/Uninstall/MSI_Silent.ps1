# MSI Silent Uninstall - Removes the application by product code
$ErrorActionPreference = 'Stop'

$ProductGUID = "{{GUID}}" # The MSI ProductCode GUID

$Arguments = @(
    '/x'
    "`"$ProductGUID`""
    '/qn'
    '/norestart'
)

$Process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $Arguments -Wait -PassThru
exit $Process.ExitCode
