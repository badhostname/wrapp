function Get-IntuneAppInventory {
    <#
    .SYNOPSIS
        Returns a lightweight list of all Intune Win32 apps for inventory display.
    .DESCRIPTION
        Wraps Get-IntuneWin32App with verbose suppressed for performance.
        Returns structured PSCustomObject items for C# consumption.
    #>
    [CmdletBinding()]
    param()

    $savedVerbose = $VerbosePreference
    $VerbosePreference = 'SilentlyContinue'

    try {
        $apps = Get-IntuneWin32App -ErrorAction Stop
    }
    finally {
        $VerbosePreference = $savedVerbose
    }

    foreach ($app in $apps) {
        [PSCustomObject]@{
            Id                = $app.id
            DisplayName       = $app.displayName
            Publisher         = $app.publisher
            AppVersion        = $app.displayVersion
            AssignmentCount   = if ($app.isAssigned) { 1 } else { 0 }
            DependencyCount   = $app.dependentAppCount
            SupersedenceCount = $app.supersedingAppCount + $app.supersededAppCount
            LastModified      = $app.lastModifiedDateTime
        }
    }
}
