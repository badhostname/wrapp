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
########################################################################
# START - Installs an App (EXE)
########################################################################

$EXEArgs = "/S"
"`r`nLaunching the App installer $PackagePath\B\$($App.EXEFile) silently with the following arguments: $EXEArgs`r`n"
Start-Process "$PackagePath\B\$($App.EXEFile)" -ArgumentList "$EXEArgs" -Wait

# Give things a moment to settle
Start-Sleep -Seconds 5

########################################################################
# END - Installs an App (EXE)
########################################################################


########################################################################
# START - Installs an App (MSI)
########################################################################

$MSIArgs = "/i ""$PackagePath\B\$($App.MSIFile)"" /quiet /norestart"
"`r`nLaunching the App installer silently with the following arguments:`r`n$MSIArgs"
Start-Process "msiexec.exe" -ArgumentList $MSIArgs -Wait

# Give things a moment to settle
Start-Sleep -Seconds 5

########################################################################
# END - Installs an App (MSI)
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
##* Creating Shortcuts (Standard approach)
##*===============================================
# Your app doesn't make a shortcut? This bit of code can create one for you.
# Create shortcut file on public desktop
$ShortcutPath = Join-Path -Path $PublicDesktopPath -ChildPath "AppName.lnk"
$shell = New-Object -ComObject WScript.Shell
$Shortcut = $shell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = "${env:ProgramFiles}\Vendor\App\App.exe"
$Shortcut.Save()

##*===============================================
##* Creating Shortcuts (Microsoft Store Apps/Uwp Apps/MSIX Apps)
##*===============================================
# Your app doesn't make a shortcut? This bit of code can create one for you.
# Create shortcut file on public desktop
$ShortcutPath = Join-Path -Path $PublicDesktopPath -ChildPath "AppName.lnk"
$LocalAppDir = "{{LocalAppFolder}}\AppName" # AppName: replace with your app's folder
$AUMID =  (Get-AppxPackage -AllUsers | Where-Object {$_.PackageFullName -like "*PACKAGENAME*"} | Select-Object PackageFamilyName) # Replace *PACKAGENAME* with yours
if ($null -eq $AUMID) { Trap-Status "AUMID is null or empty. Cannot create shortcut."}
$AUMID = $AUMID.PackageFamilyName + "!App" # See note below for !App
<#
If the AppxManifest.xml file for your app uses something that isn't (App) for an ID, replace the !App with your !ID (Keep the !).
To find the AppxManifest.xml for your app, look inside C:\Program Files\WindowsApps\ and find your app.
  <Applications>
    <Application Id="App" Executable="YourApp.exe" EntryPoint="PACKAGENAME.App">
#>

# Create shortcut file on public desktop
$shell = New-Object -ComObject WScript.Shell
$Shortcut = $shell.CreateShortcut($ShortcutPath)
$Shortcut.Arguments = "shell:AppsFolder\$AUMID"
$Shortcut.IconLocation = "$($LocalAppDir)\AppName.ico" # You'll want an icon available locally, or else the shortcut will use Windows Explorer folder as the icon.
$Shortcut.TargetPath = "C:\Windows\Explorer.exe"
$Shortcut.Save()

##*===============================================
##* Adding Reg keys
##*===============================================
# Adding Registry Key
$RegKey = "HKLM:\SOFTWARE\SCCMDetection\Vendor\App"
New-Item $RegKey -Force
# Adding Registry Key Property
New-ItemProperty -Path $RegKey -Name "AppPresent" -Value "True" -PropertyType "String" -Force

##*===============================================
##* Importing Reg keys
##*===============================================
Start-Process reg.exe -ArgumentList "import", "$PackagePath\B\your_custom_registry_file.reg" -Wait

##*===============================================
##* Creating a local folder and copying files to it
##*===============================================
$localDirectory = "C:\localDirectory\config"
"Adding necessary config files at: $localDirectory"
if(-not(Test-Path $localDirectory)){
	"`nLocal file directory for your package does not yet exist, creating it..."
	New-Item -ItemType Directory -Path $localDirectory -Force
}
""
"Moving config files to $localDirectory"
Copy-Item -Path "$($PackagePath)\B\config\*" -Destination $localDirectory -Recurse


##*===============================================
##* Special Case (Antidote)
##* Good for showcasing multiple msi, msp, mst
##*===============================================
# This specific case relies on a $Flavour Argument passed from the shortcut or command
# param ($Flavour = "NonPrism")
# Configuration-specific MST files directory
$dirMst = "$PackagePath\B\mst\config"
[Hashtable]$configMst = @{
	'OLCPCAdminB' = "$dirMst\OLCPCAdminB.mst"
	'OLCPCAdminE' = "$dirMst\OLCPCAdminE.mst"
	'OLCPCAdminF' = "$dirMst\OLCPCAdminF.mst"
	'OLCPCUser'   = "$dirMst\OLCPCUser.mst"
	'PPDAdmin'    = "$dirMst\PPDAdmin.mst"
	'PPDUser'     = "$dirMst\PPDUser.mst"
	'TRADAdmin'   = "$dirMst\TRADAdmin.mst"
	'TRADUser'    = "$dirMst\TRADUser.mst"
	'NonPrism'    = "$dirMst\ReseauAntidoteOrig.mst"
}

if(-Not $configMst[$Flavour]) #If no valid flavour specified set default to NonPrism
{
	"An invalid flavour ($Flavour) was specified so we overrode it with NonPrism"
	$Flavour = "NonPrism"
}

# Language-specific MST files
$languageMstAntidote11     = "$PackagePath\B\mst\Antidote11-Interface-$Language.mst"
$languageMstConnectix11    = "$PackagePath\B\mst\Antidote-Connectix11-Interface-$Language.mst"
$languageMstEnglishModule  = "$PackagePath\B\mst\Antidote11-English-module-Interface-$Language.mst"
$languageMstFrancaisModule = "$PackagePath\B\mst\Antidote11-Module-francais-Interface-$Language.mst"

$MSIArgs = "/i ""$PackagePath\B\Antidote11.msi"" ALLUSERS=1 TRANSFORMS=""$($configMst[$Flavour]);$languageMstAntidote11"" /quiet /norestart"
"`r`nLaunching the $Flavour Antidote base installer silently with the following arguments:`r`n$MSIArgs"
Start-Process "msiexec.exe" -ArgumentList $MSIArgs -Wait

$MSIArgs = "/i ""$PackagePath\B\Antidote-Connectix11.msi"" ALLUSERS=1 TRANSFORMS=""$dirMst\ReseauConnectix.mst;$languageMstConnectix11"" /quiet /norestart"
"`r`nLaunching the Connectix module installer silently with the following arguments:`r`n$MSIArgs"
Start-Process "msiexec.exe" -ArgumentList $MSIArgs -Wait

$MSIArgs = "/i ""$PackagePath\B\Antidote11-English-module.msi"" ALLUSERS=1 TRANSFORMS=""$languageMstEnglishModule"" /quiet /norestart"
"`r`nLaunching the Antidote English module installer silently with the following arguments:`r`n$MSIArgs"
Start-Process "msiexec.exe" -ArgumentList $MSIArgs -Wait

$MSIArgs = "/i ""$PackagePath\B\Antidote11-Module-francais.msi"" ALLUSERS=1 TRANSFORMS=""$languageMstFrancaisModule"" /quiet /norestart"
"`r`nLaunching the Antidote French module installer silently with the following arguments:`r`n$MSIArgs"
Start-Process "msiexec.exe" -ArgumentList $MSIArgs -Wait

$MSIArgs = "/p ""$PackagePath\B\Diff_Connectix_11_C_11.6_8.msp"" /quiet /norestart"
"`r`nLaunching the Connectix module patch installer silently with the following arguments:`r`n$MSIArgs"
Start-Process "msiexec.exe" -ArgumentList $MSIArgs -Wait

# Give things a moment to settle
Start-Sleep -Seconds 5

##*===============================================
##* Special Case (Office365)
##*===============================================
# Construct the configuration file based on arguments and language
[Hashtable]$configXML = @{
	'Project' = "$PackagePath\B\Configuration_silent_$($Language)_Project.xml"
	'Visio'   = "$PackagePath\B\Configuration_silent_$($Language)_Visio.xml"
	'Default' = "$PackagePath\B\Configuration_silent_$Language.xml"
}

if(-Not $configXML[$PackageOption]) #If no valid PackageOption specified
{
	Trap-Status "An invalid Option ($PackageOption) was specified, bailing out." $True
	
}

$EXEArgs = "/Configure $($configXML[$PackageOption])"
if($Platform -eq "64")
{
	$EXEArgs = $EXEArgs.replace(".xml", "_x64.xml")# make it 64bit
}

"`r`nLaunching the Office installer $PackagePath\B\Setup.exe silently with the following arguments: $EXEArgs`r`n"
Start-Process "$PackagePath\B\Setup.exe" -argumentlist "$EXEArgs" -wait

# Give things a moment to settle
Start-Sleep -Seconds 5

########################################################################
# END - Useful other bits of code to remove various things post-uninstall
########################################################################

# Give things a moment to settle
Start-Sleep -Seconds 5

########################################################################
# End of code specific to install
########################################################################
Report-Wrapup

""
"Install complete."
