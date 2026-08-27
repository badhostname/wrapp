function Get-IntuneAppFullDetail {
    <#
    .SYNOPSIS
        Returns full detail for a single Intune Win32 app including assignments,
        dependencies, and supersedence.
    .PARAMETER AppId
        The Intune app ID (GUID).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$AppId
    )

    $savedVerbose = $VerbosePreference
    $VerbosePreference = 'SilentlyContinue'

    try {
        $app         = Get-IntuneWin32App -ID $AppId -ErrorAction Stop
        $assignments = Get-IntuneWin32AppAssignment -ID $AppId -ErrorAction SilentlyContinue
        $deps        = Get-IntuneWin32AppDependency -ID $AppId -ErrorAction SilentlyContinue
        $sups        = Get-IntuneWin32AppSupersedence -ID $AppId -ErrorAction SilentlyContinue
    }
    finally {
        $VerbosePreference = $savedVerbose
    }

    # Parse detection rules
    $detType    = 'Unknown'
    $detSummary = ''
    $detScript  = ''

    if ($app.detectionRules) {
        $rule = $app.detectionRules | Select-Object -First 1
        switch ($rule.'@odata.type') {
            '#microsoft.graph.win32LobAppPowerShellScriptDetection' {
                $detType   = 'PowerShell Script'
                $detScript = [System.Text.Encoding]::UTF8.GetString(
                    [System.Convert]::FromBase64String($rule.scriptContent))
                $detSummary = "PowerShell script ($($detScript.Length) chars)"
            }
            '#microsoft.graph.win32LobAppRegistryDetection' {
                $detType    = 'Registry'
                $detSummary = "$($rule.keyPath)\$($rule.valueName) $($rule.detectionType)"
            }
            '#microsoft.graph.win32LobAppFileSystemDetection' {
                $detType    = 'File'
                $detSummary = "$($rule.path)\$($rule.fileOrFolderName) $($rule.detectionType)"
            }
            '#microsoft.graph.win32LobAppProductCodeDetection' {
                $detType    = 'MSI Product Code'
                $detSummary = "Product: $($rule.productCode)"
            }
        }
    }

    [PSCustomObject]@{
        Platform         = 'Intune'
        Id               = $app.id
        DisplayName      = $app.displayName
        Publisher        = $app.publisher
        Version          = $app.displayVersion
        Description      = $app.description
        InstallCommand   = $app.installCommandLine
        UninstallCommand = $app.uninstallCommandLine
        InstallExperience = $app.installExperience.runAsAccount
        RestartBehavior  = $app.installExperience.deviceRestartBehavior
        MaxInstallTime   = $app.maximumInstallationTimeInMinutes
        Notes            = $app.notes
        DetectionType    = $detType
        DetectionSummary = $detSummary
        DetectionScript  = $detScript
        Categories       = @($app.categories | ForEach-Object { $_.displayName })
        ReturnCodes      = @($app.returnCodes | ForEach-Object {
            [PSCustomObject]@{ Code = $_.returnCode; Type = $_.type }
        })
        Assignments      = @($assignments | ForEach-Object {
            [PSCustomObject]@{
                Intent     = $_.intent
                TargetType = $_.target.'@odata.type'
                GroupName  = $_.target.groupId
                Notification = $_.settings.notifications
            }
        })
        Dependencies     = @($deps | ForEach-Object {
            [PSCustomObject]@{
                AppId   = $_.targetId
                AppName = $_.targetDisplayName
                Type    = 'Dependency'
            }
        })
        Supersedence     = @($sups | ForEach-Object {
            [PSCustomObject]@{
                AppId   = $_.targetId
                AppName = $_.targetDisplayName
                Type    = $_.supersedenceType
            }
        })
    }
}
