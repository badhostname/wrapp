########################################################################
# Welcome to InstallScript written by Gordon P. Martin
# Version Template
#
# This script installs App
# This is a template, there's extra stuff in here you might not
# need everytime. This serves as a cookbook, feel free to change
# the recipe a little bit.
#
# InstallScript is a simple script for deploying applications
# in a standard way.  This could be used to launch an installer,
# or copy files, or make other system changes.
#
# This script can be launched via SCCM or through the Shortcuts folder.
#
# This script relies on the Appease.ps1 script to prepare the environment
# and provide standard functions.  The Config.json file provides most
# meta data and configuration information needed to perform an install.
#
########################################################################
# v1.0 - 2025-01-24 by The 3 Packeteers
#      - Initial release
########################################################################
param ([string]$PackageOption = "Default") # The $PackageOption parameter can be used by the calling package or shortcut to select an option which code below can choose to respond to.

# Script details
$ScriptType = "Install"
$ScriptVersion = "1.0"

$CallScriptPath = $script:MyInvocation.MyCommand.Path # The script path need to be passed to Appease
$CallScriptArguments = [System.Environment]::GetCommandLineArgs() # The arguments need to be passed to Appease
$CallScriptParams = $psboundparameters # The parameters need to be passed to Appease

$Global:Abort = $False # This variable is needed to tell the calling program that an Exit is required

# Run Appease to create operational environment
. $PSScriptRoot\Appease.ps1 # This "dot source" syntax runs all the commands within Appease as if they were contained within this main script.
if ($Global:Abort) {Exit $Global:StatusError} # If Appease code triggered an exit, we need to perform that exit again from this calling script.

trap { Trap-Status "Unexpected error occurred: " $_ } # Trap errors


""
"--------Application-specific code commencing----------"
""
########################################################################
# Code specific to this install...


# <Code that applies to all targeted systems goes here>

# Please note that the Config.json has DetectRunning and CloseRunning settings that can trigger
# a process in Appease.ps1 to close any running tasks before installation if that is required.

# Please note that the Config.json has a UninstallFirst setting that can force the Uninstall
# to run before this installation continues.

# A good guide for properly using MSI installer arguments: 
#     https://learn.microsoft.com/en-us/windows/win32/msi/standard-installer-command-line-options
########################################################################

"This script installs $($app.name)"
""

"Package Option: $PackageOption"

# MSI Silent Install - Installs {{MSIFile}} with quiet mode and no restart

$MSIFile = "{{MSIFile}}"
$InstallerPath = Join-Path -Path $PSScriptRoot -ChildPath "..\B\$MSIFile"

if (-not (Test-Path $InstallerPath)) {
    Write-Error "Installer not found: $InstallerPath"
    Trap-Status "Installer not found: $InstallerPath"
}

$Arguments = @(
    '/i'
    "`"$InstallerPath`""
    '/qn'
    '/norestart'
    'ALLUSERS=1'
)

$Process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $Arguments -Wait -PassThru

# Give things a moment to settle
Start-Sleep -Seconds 5

########################################################################
# End of code specific to install
########################################################################
Report-Wrapup

""
"Install complete."
