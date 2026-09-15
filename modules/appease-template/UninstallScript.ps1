########################################################################
# Welcome to UninstallScript written by Gordon P. Martin
# Version Template
# This is a template , there's extra stuff in here you might not
# need everytime. This serves as a cookbook, feel free to changes
# the recipe a little bit. There are some niche but useful examples
# you can use to reproduce certain situations.
#
# UninstallScript is a simple script for uninstalling applications
# in a standard way.
#
# SCCM relies on scripts like this when there isn't a suitable
# MSI installer available.
#
# This particular version uninstalls App
#
########################################################################
# v1.0 - 2025-01-24 by The 3 Packeteers
#      - Initial release
########################################################################
param ([string]$PackageOption = "Default") # The $PackageOption parameter can be used by the calling package or shortcut to select an option which code below can choose to respond to.

$ScriptType = "Uninstall"
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
# Code specific to this uninstall...

# <Code that applies to all targeted systems goes here>

# Please note that the Config.json has DetectRunning and CloseRunning settings that can trigger
# a process in Appease.ps1 to close any running tasks before uninstallation if that is required.

# A good guide for properly using MSI installer arguments: 
#     https://learn.microsoft.com/en-us/windows/win32/msi/standard-installer-command-line-options
########################################################################

"This script uninstalls $($app.name)"
""

"Package Option: $PackageOption"

########################################################################
# START - Uninstalls an App that exists in the Uninstall keys (Control Panel)
# This is the most common case for uninstallscript.ps1.
########################################################################
Uninstall-App "{{Name}}*"
########################################################################
# END - Uninstalls an App that exists in the Uninstall keys (Control Panel)
########################################################################


########################################################################
# START - Useful other bits of code to remove various things post-uninstall
########################################################################
##*===============================================
##* Useful Variables
##*===============================================
$PublicDesktopPath = [Environment]::GetFolderPath("CommonDesktopDirectory") # Path to Public Desktop (common)
$envShellFolders = Get-ItemProperty -Path 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' -ErrorAction 'SilentlyContinue'
$envCommonStartMenu = $envShellFolders | Select-Object -ExpandProperty 'Common Start Menu' -ErrorAction 'SilentlyContinue' # Path to startmenu
$envCommonStartMenuPrograms = $envShellFolders | Select-Object -ExpandProperty 'Common Programs' -ErrorAction 'SilentlyContinue' # Path to startmenu programs
$envUserDesktop = [Environment]::GetFolderPath('DesktopDirectory') # Get the desktop for the user (useful for user context installs)
$Language = $PSUICulture.substring(0,2) # Extract two letter OS language (e.g. en from en-us)

##*===============================================
##* Removing Shortcuts
##*===============================================
# Removing a known shortcut & Folder from the public start menu programs if the uninstaller does not take care of it.
# This example is for Power BI Desktop
if (Test-Path "$envCommonStartMenuPrograms\Microsoft Power BI Desktop") {
	"`nFound Leftover Shortcut on $envCommonStartMenuPrograms for $($App.Name), removing it..."
    Remove-Item "$envCommonStartMenuPrograms\Microsoft Power BI Desktop" -Force -Recurse
}

##*===============================================
##* Removing reg keys
##*===============================================
# Removing Registry Key and sub properties
Remove-Item "HKLM:\SOFTWARE\SCCMDetection\Vendor\App" -Recurse -ErrorAction SilentlyContinue
# Removing Registry Key Property
Remove-ItemProperty -Path "HKLM:\SOFTWARE\SCCMDetection\Vendor\App" -Name "AppPresent"-ErrorAction SilentlyContinue

##*===============================================
##* Special Case
##*===============================================
# Some cases where using the uninstall-app function isn't enough, we need clever lookups.
# This example is for RocketSoftware Folio Views.
$InstalledVersions = Get-RegisteredApps -Filter "Folio Views*"
$InstalledGUIDS = ($InstalledVersions).PSChildName
$EXEArgs = "/s /f1""$PackagePath\B\RemoveEN.iss"""

if ($PSUICulture -like "fr-*") # French OS needs different .mst and bits
{
	$EXEArgs = $EXEArgs.replace("EN.iss", "FR.iss") # make it french
}

# We can't rely on the uninstall-app function in this case because the uninstall string supplied
# in the registry does not perform a full uninstall. Using the setup.exe with /ig{GUID} switch
# instead
foreach($GUID in $InstalledGUIDS) {
	$GUIDArgs = "$EXEArgs /ig$guid" # we use the original EXEArgs but add the guid after the /ig switch (provided by vendor documentation)
	"`r`nLaunching the Folio uninstaller silently with the following arguments:`r`n$($GUIDArgs)"
	Start-Process "$PackagePath\B\$($App.EXEFile)" -argumentlist "$GUIDArgs" -wait
}

##*===============================================
##* Special Case (Office365)
##*===============================================
# Construct the configuration file based on arguments and language
[Hashtable]$configXML = @{
	'Project' = "$PackagePath\B\Remove_silent_Project.xml"
	'Visio'   = "$PackagePath\B\Remove_silent_Visio.xml"
	'Default' = "$PackagePath\B\Remove_silent.xml"
}

if(-Not $configXML[$PackageOption]) #If no valid PackageOption specified set default to NonPrism
{
	Trap-Status "An invalid Option ($PackageOption) was specified, bailing out." $True
	
}

$EXEArgs = "/Configure $($configXML[$PackageOption])" # Based on the $PackageOption provided and the xml based on that package we modify what we're using to config the install

"`r`nLaunching the Office uninstaller $PackagePath\B\Setup.exe silently with the following arguments: $EXEArgs`r`n"
Start-Process "$PackagePath\B\Setup.exe" -argumentlist "$EXEArgs" -wait

# Give things a moment to settle
Start-Sleep -Seconds 5

########################################################################
# END - Useful other bits of code to remove various things post-uninstall
########################################################################

# Give things a moment to settle
Start-Sleep -Seconds 5

########################################################################
#End of code specific to uninstall
########################################################################
Report-Wrapup

""
"Uninstall complete."

