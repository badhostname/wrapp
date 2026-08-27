@{
    RootModule        = 'Wrapp.Packager.psm1'
    ModuleVersion     = '4.0.0'
    GUID              = 'a7fc85cf-2c15-4a7d-8e36-50a52986c81c'
    Author            = 'badhostname'
    CompanyName       = 'Wrapp'
    Copyright         = '(c) 2026 badhostname. All rights reserved.'
    Description       = 'Wrapp.Packager - Win32 application packaging module for Microsoft Intune and SCCM. Supports app creation, detection rules, requirement rules, dependencies, supersedence, assignments, and content distribution with CMTrace logging. Powers the Wrapp GUI and is fully CLI-capable via Invoke-WrappPackaging.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        'Invoke-WrappPackaging',
        'Invoke-WrappIntune',
        'Invoke-WrappSccm',
        'Connect-WrappIntune',
        'Connect-WrappSccm',
        'Test-WrappConfig',
        'Test-WrappIntunePreflight',
        'Test-WrappSccmPreflight',
        'New-IntuneWin32Package',
        'Add-IntuneWin32AppFromConfig',
        'Update-IntuneWin32AppFromConfig',
        'Remove-IntuneWin32AppSafe',
        'Set-Win32AppAssignment',
        'Test-Win32AppCollisions',
        'Add-CMAppFromConfig',
        'Set-CMAppDeployment',
        'Test-CMAppCollisions'
    )
    CmdletsToExport   = @()
    VariablesToExport  = @()
    # Legacy pre-Wrapp names (IntunePackager / SCCMPackager script era) kept as
    # aliases so existing CLI scripts keep working. Defined in the psm1 loader.
    AliasesToExport    = @(
        'Invoke-IntunePackager',
        'Invoke-SCCMPackager',
        'Connect-IntunePackager',
        'Connect-SCCMPackager',
        'Test-PackagerConfig',
        'Test-IntunePackagerPreflight',
        'Test-SCCMPackagerPreflight'
    )

    PrivateData = @{
        PSData = @{
            Tags = @(
                'Wrapp',
                'Intune',
                'SCCM',
                'Win32',
                'AppPackaging',
                'MDM',
                'ConfigurationManager',
                'IntuneWin32App'
            )
            LicenseUri   = ''
            ProjectUri   = 'https://github.com/badhostname/wrapp'
            ReleaseNotes = ''
        }
    }
}
