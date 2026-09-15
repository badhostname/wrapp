########################################################################
# Welcome to Appease written by Gordon P. Martin
# Version 2.3
#
# Appease is the helper app that creates the operational environment
# for the InstallScript and UninstallScript.  It collects information
# about the host OS, PC and domain.  It passes in contextual/meta data 
# from the Config.json file.  It handles process elevation and houses
# many standardized functions to simplify the main scripts.
# 
# Appease provides a standardized framework to aid in the creation
# of scripted application packages without the need for extensive
# scripting experience.
#
# Appease and its associated file structure creates a standardized
# installation experience whether launching the installation manually
# or through SCCM.  The installation can adapt to different
# domains and operating systems so that one installer package can
# operate everywhere properly.
########################################################################
# v2.3 - 2025-06-10
#      - Fixed a bug in Uninstall-App that failed to parse the 
#        UninstallString if parameters were present
#        (Now parsed just like QuietUninstallString)
#      - Added Task sequence detection to identify apps installed via MDT
# v2.2 - 2025-02-07 - Gordon Martin
#      - Copied the Unblock-File logic from SCCMPackager into
#        an Unblock-Package function that can be used to unblock files 
#        any time without having to wait for the packaging step.
#      - Added -Services to the default Get-SystemSnapshot in order to detect
#        installation of weird applications like Sysmon.
#      - Updated the console mode welcome message for the recent changes 
#        and to suggest the usage of ${}.
#      - Improved Report-Wrapup messaging.
# v2.1 - 2025-01-30
#      - Added logic to adjust console buffer size for when we set the
#        console dimensions in $host.UI.RawUI.WindowSize.
#        This addresses running installer in console mode in VMM.
# v2.0 - 2024-12-23
#      - Added support for multiple Detection Expressions that can be
#        utilized by the DetectScript and SCCMPackager.
#      - Reworked the system snapshot and comparison functionality again:
#        Deleted: Compare-RegisteredApps, Get-Snapshot, Compare-Snapshot
#        Created: Get-FileSnapshot, Get-ServiceSnapshot, Get-ProcessSnapshot, 
#          Get-SystemSnapshot, Add-SystemSnapshot, Get-ComparisonProperties, 
#          Compare-SnapshotObjects, Compare-SystemSnapshot, Output-SystemComparison,
#          Report-Changes
#      - Modified Get-FileSnapshot to collect data from more paths and to report
#        extended information for .lnk shortcut files (such as icon and target).
# v1.9 - 2024-12-06
#      - Added enforcement of the App.Dependencies defined in the config.json
#        so that installation only continues if dependencies are met.
#      - Reworked Get-Snapshot and Compare-Snapshot to handle
#        VersionInfo.
# v1.8 - 2024-11-15
#      - Added Report-Wrapup to hide standard finishing commands
#        for InstallScript and UninstallScript.
#      - Added call to DetectScript (always good to run a detection)
#        and enforce the $Script.DetectApp property.
#      - Added Get-DirectorySnapshot to capture and track snapshots of
#        files and directories up to a specified depth, allowing detailed
#        system state tracking before and after package installations.
#      - Added Compare-Snapshots to efficiently identify and present 
#        new and removed items by comparing directory snapshots.
#        Supports filtering of strings for focused comparisons.
#      - Convert script drive letter path to unc path to avoid breakage
#        when elevating.
#      - Added Get-RobocopyProgress to display progress bar when
# 	     copying files to local cache.
#      - Avoid Start-Transcript errors if a transcript is already running
#        (Clean close).
# v1.7 - 2024-10-17 - Gordon Martin
#      - Renamed Get-InstalledApplication to Get-RegisteredApps
#      - Created Compare-RegisteredApps to facilitate package authoring
#      - Renamed Uninstall-MSI to Uninstall-App
#      - Improved Uninstall-App to automate uninstall of MSI and Setup.exe.
#        Added new parameters to enhance flexibility.
#      - Added support for a "Console" ScriptType when Appease is 
#        launched directly rather than by another script.
#      - Added a welcome message for "Console" that offers Function
#        suggestions for the operator.
# v1.6 - 2024-10-15 - Trevor Ritchie
#	   - Modified Get-InstalledApplication to return all properties
# v1.5 - 2024-10-07
#	   - Added Get-InstalledApplication to speed up MSI Lookup
#      - Modified Uninstall-MSI to use this lookup instead of 
#      - Get-WmiObject Win32_Product
#	   - Changed Stop-TranscriptPlus to use SID
# v1.4 - 2024-09-25 - Gordon Martin
#      - Added local package caching to support admin accounts that don't
#        have network share access.
#      - Added support for non-domain joined computers.  Appease will try
#        to use user rights to find a suitable domain defined in Config.json.
# v1.3 - 2024-07-19 - Gordon Martin
#      - Added support for non domain joined PCs - including Azure / Intune
# v1.2 - 2024-07-16 - Trevor Ritchie
#      - Added support for French OS
# v1.1 - 2024-05-07 by Gordon Martin
#	   - Changed the central tag file renaming to include the user 
#        that ran the Appease package.
#      - Changed the Uninstall-MSI function to support searches
#        by both product name or GUID.  This supports both a surgical
#        or scattergun approach to uninstalling (a specific version vs
#        all previous versions).
# v1.0 - April 24, 2024 by Gordon Martin
#      - Initial release - replacement for SRDInstaller
########################################################################
$AppeaseVersion = "2.3"


########################################################################
# Stop-TranscriptPlus handles graceful shutdown of Transcript logging.
# It also fixes log permission issues when running elevated.
########################################################################
Function Stop-TranscriptPlus
{
  If ($Transcript) # Only perform work if $Transcript is running...
  {
    try # Avoid an error if there is no transcript running...
    {
		stop-transcript|out-null

		# Ensure that the local Users group has full rights to the transcript log file...
		# This ensures that various processes don't experience an error in the future.

		$NewAcl = Get-Acl -Path $LogFile # Preserve existing permissions
		$usersGroup = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-545") # BUILTIN\Users group, so we avoid language issues
		$FSAccessRule = New-Object System.Security.AccessControl.FileSystemAccessRule ($usersGroup, "FullControl", "Allow")
	
		$NewAcl.SetAccessRule($FSAccessRule)
		# Set permission
		Set-Acl -Path $LogFile -AclObject $NewAcl
    }
    catch [System.InvalidOperationException]{}
	
	If ($Domain.TagFolder -and ($ScriptType -ne "Console")) # A central Tag folder has been specified (not to be used when running from console)...
	{
		"A central share has been specified for a copy of the log file: $($Domain.TagFolder)"
		if (Test-Path $Domain.TagFolder)
		{
			$CentralFile = "$($Domain.TagFolder)\$($Script.Tag)\$($env:ComputerName)_$($LaunchProcessType)_$($env:UserName).TFR"

			"Copying the log to $CentralFile"
			New-Item -ItemType File -Path $CentralFile -Force # This trick will cause any necessary parent folders to be created (which doesn't happen when Copy-Item is just copying files)
			Copy-Item $LogFile -Destination $CentralFile -Force

		}
		Else
		{ 
			"Error: Could not find $($Domain.TagFolder) folder."
		}

	}
  }
}


########################################################################
# Trap-Status handles messages and errors to provide extra control
########################################################################
Function Trap-Status($StatusMessage, $StatusError)
{
  $Global:StatusError = $StatusError
  
  # "`a " # This is a neat special character - it makes the console beep!

  if ($StatusError) # Report an error
  {
    $OldForeColor = [console]::ForegroundColor
    [console]::ForegroundColor = "Red"
    ""
    $StatusMessage
    ""
    $script:Exception = $StatusError
    "with error:"
    $StatusError
    [console]::ForegroundColor = $OldForeColor
    ""
  }
  else #Report a normal message
  {
    ""
    $StatusMessage
    ""
  }

  Stop-TranscriptPlus

  If ($UseCache) # There is a cached package that needs to be cleaned up...
  {
	"Deleting the temporary cache files" 
	Remove-Item $CachePackagePath -Force -Recurse
  }

  $Global:Abort = $True # This variable is needed to tell the calling program that an Exit is required
  Exit $Global:StatusError
}



########################################################################
# Unblock-Package performs the Unblock_File command where necessary.
# Files that get downloaded from the internet can get blocked by the OS because they are considered a security risk and unsafe.
# If you trust the source and plan to distribute it in packages, it is best to remove that property
# The file property in question is Zone.Identifier...
########################################################################
Function Unblock-Package 
{
	""
	"Scanning package for any vendor source files that may be blocked due to past internet downloads:"

	$NoBlock = $True
	Foreach ($Blocked in (Get-ChildItem $PackagePath -recurse | get-item -Stream "Zone.Identifier" -ErrorAction SilentlyContinue))
	{
		"  Unblocking downloaded file: $($Blocked.FileName)"
		$NoBlock = $False
		Unblock-File $Blocked.FileName
	}
	If ($NoBlock){"  No blocked files found - all good."}
	""
}



########################################################################
# Get-RegisteredApps handles looking up installed Apps using
# well-known app install locations in the registry
########################################################################
Function Get-RegisteredApps {
    [CmdletBinding()]
    Param (
        [Parameter(Mandatory = $false)]
        [ValidateNotNullorEmpty()]
        [String[]]$Filter
    )

    Begin {
        ## Variables: Registry Keys
        #  Registry keys for native and WOW64 applications
        [String[]]$regKeyApplications = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    }
    Process {
		
		If ($VerboseMode) {Write-Host "(Getting Apps snapshot)"} # Report debug details 

        ## Enumerate the installed applications from the registry for applications that have the "DisplayName" property
        [PSObject[]]$regKeyApplication = ForEach ($regKey in $regKeyApplications) {
            If (Test-Path -LiteralPath $regKey -ErrorAction 'SilentlyContinue') {
                [PSObject[]]$UninstallKeyApps = Get-ChildItem -LiteralPath $regKey -ErrorAction 'SilentlyContinue'
                ForEach ($UninstallKeyApp in $UninstallKeyApps) {
                    Try {
                        [PSObject]$regKeyApplicationProps = Get-ItemProperty -LiteralPath $UninstallKeyApp.PSPath -ErrorAction 'Stop'
                        If ($regKeyApplicationProps | Select-Object -ExpandProperty DisplayName -ErrorAction SilentlyContinue) {
                            $regKeyApplicationProps
                        }
                    }
                    Catch {
                        #"Unable to enumerate properties from registry key path [$($UninstallKeyApp.PSPath)]."
                        Continue
                    }
                }
            }
        }

        ## Create a custom object with the available properties for the installed applications and sanitize property details
        [PSObject[]]$installedApplication = @()
        ForEach ($regKeyApp in $regKeyApplication) {
            Try {
                ## Sanitize the DisplayName, DisplayVersion, and Publisher properties
                [String]$appDisplayName = $regKeyApp.DisplayName -replace '[^\p{L}\p{Nd}\p{Z}\p{P}]', ''
                [String]$appDisplayVersion = ($regKeyApp | Select-Object -ExpandProperty DisplayVersion -ErrorAction SilentlyContinue) -replace '[^\p{L}\p{Nd}\p{Z}\p{P}]', ''
                [String]$appPublisher = ($regKeyApp | Select-Object -ExpandProperty Publisher -ErrorAction SilentlyContinue) -replace '[^\p{L}\p{Nd}\p{Z}\p{P}]', ''

                # Initialize an empty hashtable to dynamically add properties
                $appProperties = @{}

                # Iterate over all properties of the registry key and add them dynamically
                foreach ($property in $regKeyApp.PSObject.Properties) {
                    $appProperties[$property.Name] = $property.Value
                }

                # Sort the hashtable by keys and create an ordered hashtable
                $orderedAppProperties = [ordered]@{}
                foreach ($key in ($appProperties.Keys | Sort-Object)) {
                    $orderedAppProperties[$key] = $appProperties[$key]
                }

                # Add sanitized values
                $orderedAppProperties['DisplayName'] = $appDisplayName
                $orderedAppProperties['DisplayVersion'] = $appDisplayVersion
                $orderedAppProperties['Publisher'] = $appPublisher
				$orderedAppProperties['ZZDeliniation'] = "#########################################################################"
				
                # Verify if there is a match with the product code or DisplayName passed to the script
                If (($regKeyApp.PSChildName -like $Filter) -or ($regKeyApp.DisplayName -like $Filter)) {
                    $installedApplication += New-Object -TypeName 'PSObject' -Property $orderedAppProperties
                }
            }
            Catch {
                Continue
            }
        }
        return $installedApplication
    }

    End {
    }
}


########################################################################
# Get-FileSnapshot captures and tracks snapshots of files and directories
# up to a specified depth, allowing detailed system state tracking before 
# and after package installations.
#
# Parameters:
#   -Paths: The root paths to begin the snapshot from. Default is 'C:\Program Files', 'C:\Program Files (x86)'
#   -MaxDepth: The maximum depth to recurse into directories. Default is 4.
#   -Full: Switch to include full VersionInfo for files.
########################################################################
Function Get-FileSnapshot {
    [CmdletBinding()]
    Param(
        [Parameter(Mandatory=$false)][string[]]$Paths = @(
			"$Env:ProgramFiles"
			"${Env:ProgramFiles(x86)}"
			"$($Env:ProgramData)\Microsoft\Windows\Start Menu\Programs"
			"$($Env:SystemRoot)\System32"
			),
        [Parameter(Mandatory=$false)][int]$MaxDepth = 4,
        [Parameter(Mandatory=$false)][switch]$Full = $False
    )
	# Some optional paths to consider using:
	#		"$($Env:ProgramData)"
	#		"$($Env:SystemRoot)"
	
	
    Begin {
        [System.Collections.Generic.List[PSCustomObject]]$snapshot = @()
    }

    Process {
		If ($VerboseMode) {Write-Host "(Getting Files snapshot)"} # Report debug details 
        try {
            $Items = Get-ChildItem -Path $Paths -Recurse -Depth $MaxDepth -ErrorAction SilentlyContinue -Force
            foreach ($Item in $Items) {
                $Metadata = [ordered]@{
                    Path       = $Item.FullName
                    ParentPath = $Item.DirectoryName
                    Name       = $Item.Name
                    Type       = if ($Item.PSIsContainer) { "Folder" } else { "File" }
                    Depth      = ($Item.FullName -split '\\').Count
                }

                if (-not $Item.PSIsContainer) {
                    $Metadata.LastWrite = $Item.LastWriteTime
                    $Metadata.Length    = $Item.Length
                    $Metadata.Extension = $Item.Extension

                    if ($Full -or ($Item.Extension -eq ".exe")) # Let's add VersionInfo information...
					{
                        try {
                            foreach ($VerInfo in $Item.VersionInfo.PSObject.Properties) 
							{
                                $Metadata["VersionInfo.$($VerInfo.Name)"] = $VerInfo.Value
                            }
                        } catch {}
                    }
                    if ($Item.Extension -eq ".lnk") # Let's add shortcut information for lnk shortcut files...
					{
                        try {
                            foreach ($LNKInfo in $objShell.CreateShortcut($Item.FullName).PSObject.Properties) 
							{
                                $Metadata["ShortcutInfo.$($LNKInfo.Name)"] = $LNKInfo.Value
                            }
                        } catch {}
                    }
                }

                $Metadata['ZZDeliniation'] = "#########################################################################"
                $snapshot.Add([PSCustomObject]$Metadata)
            }
        } catch {
            Write-Warning "Error processing paths: $_"
        }
    }

    End {
        return $snapshot.ToArray()
    }
}


########################################################################
# Get-ServiceSnapshot captures the current services state
#
# No parameters. Returns a list of services with basic properties.
########################################################################
Function Get-ServiceSnapshot {
    [CmdletBinding()]
    Param()
	If ($VerboseMode) {Write-Host "(Getting Services snapshot)"} # Report debug details 
    $Services = Get-Service | Select-Object Name, Status, DisplayName, StartType, DependentServices, ServicesDependedOn
    return $Services
}

########################################################################
# Get-ProcessSnapshot captures the current processes state
#
# No parameters. Returns a list of processes with basic properties.
########################################################################
Function Get-ProcessSnapshot {
    [CmdletBinding()]
    Param()
	If ($VerboseMode) {Write-Host "(Getting Processes snapshot)"} # Report debug details 
    $Processes = Get-Process | Select-Object Name, Id, Path, StartTime
    return $Processes
}

########################################################################
# Get-SystemSnapshot collects multiple snapshots (Files, Apps, Services, Processes)
# based on requested types.
#
# Parameters:
#   -Files
#   -Apps
#   -Services
#   -Processes
########################################################################
Function Get-SystemSnapshot {
    [CmdletBinding()]
    param(
		[switch]$Files = $False,
		[switch]$Apps = $False,
		[switch]$Services = $False,
		[switch]$Processes = $False
    )

	$All = -Not ($Files -or $Apps -or $Services -or $Processes) # $All is only set if none of the flags are set (we default to doing all of them).

    $Result = [ordered]@{}

    if ($Files -or $All) 
	{
        $Result.Files = Get-FileSnapshot 
    }
    if ($Apps -or $All) 
	{
        $Result.Apps = Get-RegisteredApps "*"
    }
    if ($Services -or $All) 
	{
        $Result.Services = Get-ServiceSnapshot
    }
    if ($Processes -or $All) 
	{
        $Result.Processes = Get-ProcessSnapshot
    }

    return $Result
}

########################################################################
# Add-SystemSnapshot allows adding custom snapshot data to an existing snapshot.
#
# Parameters:
#   -Snapshot: The hashtable returned by Get-SystemSnapshot representing 
#              the current system snapshot.
#   -Name: A string name under which the custom data will be stored.
#   -Data: The array of PSCustomObject to add. Must be an array of PSCustomObject.
#
# Usage:
#   $Initial = Get-SystemSnapshot -Apps -Services -Files -Processes
#   $CustomData = Get-CustomDataFunction
#   Add-SystemSnapshot -Snapshot $Initial -Name "CustomType" -Data $CustomData
#
#   # Later after changes
#   $Final = Get-SystemSnapshot -Apps -Services -Files -Processes
#   $Final = Add-SystemSnapshot -Snapshot $Final -Name "CustomType" -Data $CustomDataAfter
#
#   $Diff = Compare-SystemSnapshot -BeforeState $Initial -AfterState $Final
#   Output-SystemComparison -ComparisonResult $Diff
########################################################################
Function Add-SystemSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [ValidateNotNull()]
        [System.Collections.IDictionary]$Snapshot,

        [Parameter(Mandatory=$true)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory=$true)]
        [ValidateNotNull()]
        [PSCustomObject[]]$Data
    )

    # Validate that $Data is an array of PSCustomObject
    if (-not ($Data -is [PSCustomObject[]])) {
        Throw "Add-SystemSnapshot: The provided Data must be an array of PSCustomObject."
    }

    # Add or overwrite the specified name in the snapshot with the new data
    $Snapshot[$Name] = $Data

    # Write-Verbose "Added custom snapshot data under '$Name' with $($Data.Count) item(s)."

    return $Snapshot
}


########################################################################
# Get-ComparisonProperties determines which properties to compare for a given snapshot type.
#
# Parameters:
#   -SnapshotType: The snapshot type (e.g., 'Files','Apps','Services','Processes', 'StartMenuContent')
#   -Before: The 'before' array of objects
#   -After: The 'after' array of objects
########################################################################
Function Get-ComparisonProperties {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]$SnapshotType,

        [Parameter(Mandatory=$true)]
        [PSObject[]]$Before,

        [Parameter(Mandatory=$true)]
        [PSObject[]]$After
    )

	########################################################################
	# Property sets for known snapshot types
	########################################################################
	$SnapshotPropertySets = @{
		"Files"        = @('Path','Name','Type','LastWrite','Length','Extension','VersionInfoFileVersionRaw')
		"Apps"             = @('Displayname','DisplayVersion','Publisher','Language','InstallLocation','DisplayIcon','PSPath','PSChildName','QuietUninstallString','UninstallString','URLInfoAbout','HelpLink','WindowsInstaller','Comments')
		"Services"         = @('Name','StartType','DisplayName','DependentServices','ServicesDependedOn')
		"Processes"        = @('Name','Id','StartTime','Path')
	}

    if ($SnapshotPropertySets.ContainsKey($SnapshotType)) {
		# If ($VerboseMode) {"Using predefined properties for snapshot type '$SnapshotType'."} # Report debug details 
        return $SnapshotPropertySets[$SnapshotType]
    } else {
		# If ($VerboseMode) {"Retrieving properties dynamically for snapshot type '$SnapshotType'."} # Report debug details 

        # Explicitly check if $Before has items
        if ($Before.Count -gt 0 -and $Before[0] -ne $null) {
            $SampleObj = $Before[0]
			# If ($VerboseMode) {"Using first object from 'Before' for property extraction."} # Report debug details 
        }
        # If $Before is empty or null, use $After
        elseif ($After.Count -gt 0 -and $After[0] -ne $null) {
            $SampleObj = $After[0]
			# If ($VerboseMode) {"Using first object from 'After' for property extraction."} # Report debug details 
        }
        else {
			If ($VerboseMode) {"Both 'Before' and 'After' snapshots are empty or contain only nulls for snapshot type '$SnapshotType'."} # Report debug details 
            return @()
        }

        # Retrieve property names from the sample object
        $Properties = ($SampleObj | Get-Member -MemberType Properties).Name
		If ($VerboseMode) {"Properties retrieved for snapshot type '$SnapshotType': $Properties"} # Report debug details 

        return $Properties
    }
}


########################################################################
# Compare-SnapshotObjects compares two arrays of objects of the same snapshot type.
#
# Parameters:
#   -Before: The 'before' array
#   -After: The 'after' array
#   -SnapshotType: The snapshot type for property selection
########################################################################
Function Compare-SnapshotObjects {
    param(
        [Parameter(Mandatory=$true)]
        [PSObject[]]$Before,
        [Parameter(Mandatory=$true)]
        [PSObject[]]$After,
        [Parameter(Mandatory=$true)]
        [string]$SnapshotType
    )

    $Props = Get-ComparisonProperties -SnapshotType $SnapshotType -Before $Before -After $After -Verbose

    if ($Props.Count -eq 0) {
        return [ordered]@{ Added=@(); Removed=@() }
    }

    $Changes = Compare-Object -ReferenceObject $Before -DifferenceObject $After -Property $Props -PassThru
    $Added   = $Changes | Where-Object SideIndicator -eq '=>'
    $Removed = $Changes | Where-Object SideIndicator -eq '<='

    return [ordered]@{ Added=$Added; Removed=$Removed }
}

########################################################################
# Compare-SystemSnapshot compares two snapshot states produced by Get-SystemSnapshot.
#
# Parameters:
#   -BeforeState: The hashtable from Get-SystemSnapshot before changes
#   -AfterState: The hashtable from Get-SystemSnapshot after changes
########################################################################
Function Compare-SystemSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][ValidateNotNull()]$BeforeState,
        [Parameter(Mandatory=$true)][ValidateNotNull()]$AfterState
    )

    $ComparisonResult = [ordered]@{}

    foreach ($Key in $BeforeState.Keys) {
        if ($AfterState.Keys -contains $Key) {
            $ComparisonResult[$Key] = Compare-SnapshotObjects -Before $BeforeState[$Key] -After $AfterState[$Key] -SnapshotType $Key
        } else {
            $ComparisonResult[$Key] = [ordered]@{Added=@();Removed=$BeforeState[$Key]}
        }
    }

    foreach ($Key in $AfterState.Keys) {
        if (-not ($BeforeState.Keys -contains $Key)) {
            $ComparisonResult[$Key] = [ordered]@{Added=$AfterState[$Key];Removed=@()}
        }
    }

    return $ComparisonResult
}

########################################################################
# Output-Items outputs the changes detected for a given snapshot type.
#
# Parameters:
#   -Items: The items to output (Added or Removed arrays)
#   -Title: The title to display
#   -SnapshotType: The snapshot type ('Files','Apps','Services','Processes', etc.)
########################################################################
Function Output-Items {
    param(
        [Parameter(Mandatory=$true)]
        [AllowEmptyCollection()]
        [AllowNull()]
        $Items,

        [Parameter(Mandatory=$true)]
        [string]$Title,

        [Parameter(Mandatory=$false)]
        [string]$SnapshotType = 'Files'
    )

    # If no items, do nothing and return
    if (-not $Items -or $Items.Count -eq 0) {
        return
    }

    "########################################################################"
    "$Title"
    "########################################################################"

    switch ($SnapshotType) {
        'Files' {
            $Items = $Items | Sort-Object Path, Depth, @{Expression = { if ($_.Type -eq 'Folder') {0} else {1} }}, Name

            $OutputtedPaths = @{}
            $FoldersToOutput = $null

            foreach ($Item in $Items) {
                $ItemType   = $Item.Type
                $ItemPath   = $Item.Path
                $ParentPath = $Item.ParentPath

                $FoldersToOutput = New-Object System.Collections.Generic.List[string]

                $CurrPath = $ParentPath
                while ($CurrPath -and (-not $OutputtedPaths.ContainsKey($CurrPath))) {
                    $FoldersToOutput.Insert(0, $CurrPath)
                    $OutputtedPaths[$CurrPath] = $true
                    $CurrPath = Split-Path -Path $CurrPath -Parent
                }

                foreach ($Folder in $FoldersToOutput) {
                    ""
                    "Folder: $Folder"
                }

                if ($ItemType -eq 'Folder') {
                    if (-not $OutputtedPaths.ContainsKey($ItemPath)) {
                        ""
                        "Folder: $ItemPath"
                        $OutputtedPaths[$ItemPath] = $true
                    }
                } else {
                    "`t$($Item.LastWrite)`t$($Item.Length)`t$($Item.Name)"
                    foreach ($Property in $Item.PSObject.Properties | Where-Object { $_.Name -like 'VersionInfo.*' }) {
                        "`t`t$($Property.Name)`t: $($Property.Value)"
                    }
                }
            }
        }
        default {
            # Just output items directly for non-file types
            ""
            foreach ($Item in $Items) {
                ($Item | Format-List -Property * | Out-String).Trim()
				""
            }
        }
    }
    ""
}

########################################################################
# Output-SystemComparison takes the $ComparisonResult from Compare-SystemSnapshot
# and outputs each snapshot type's Added and Removed items automatically.
#
# Parameters:
#   -ComparisonResult: The result from Compare-SystemSnapshot
########################################################################
Function Output-SystemComparison {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        $ComparisonResult
    )

    foreach ($Key in $ComparisonResult.Keys) {
        $SnapshotType = $Key
        $AddedArray = $ComparisonResult[$Key].Added
        $RemovedArray = $ComparisonResult[$Key].Removed

        Output-Items -Items $AddedArray -Title "$Key Added:" -SnapshotType $SnapshotType
        Output-Items -Items $RemovedArray -Title "$Key Removed:" -SnapshotType $SnapshotType
    }
}


########################################################################
# Report-Changes provides a quick compare of the current system state 
# to a previous snapshot and outputs the results.
#
# Parameters:
#   -Before $Snapshot : The hashtable from Get-SystemSnapshot before changes
########################################################################
Function Report-Changes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][ValidateNotNull()]$Before
    )

	# Figure out what the previous snapshot contained and ask for the same thing in the latest snapshot...
	$SnapshotArgs = @{
		Files = $(If ($Before.Files) {$True} else {$False})
		Apps = $(If ($Before.Apps) {$True} else {$False})
		Services = $(If ($Before.Services) {$True} else {$False})
		Processes = $(If ($Before.Processes) {$True} else {$False})
	}

	$Latest = Get-SystemSnapshot @SnapshotArgs
	
	# Compare the provided snapshot to the Latest one:
	$Change = Compare-SystemSnapshot -BeforeState $Before -AfterState $Latest
	
	# Output the results:
	Output-SystemComparison -ComparisonResult $Change
	
    return
}


########################################################################
# Report-Wrapup this function is called at the Install and uninstall
# scripts to hide and standardize some of the final commands.
# These commands are meant to output various reports used to Confirm
# whether an application has been properly installed or uninstalled.
# It will also trigger the proper closing and archiving of the transcript
########################################################################
Function Report-Wrapup
{
	""	
	"Performing Report Wrapup to confirm changes occurred..."
	""
	# Report any registered application changes in Windows...
	
	Report-Changes $Before # This compares the Before snapshot with a new one.


	# Run the app detection and respond if the results aren't as expected...
	
	If ($AppDetected = & "$ScriptPath\DetectScript.ps1" $PackageOption) # App detected as installed...
	{
		If ($ScriptType -eq "Uninstall")
		{
			""
			"Something went wrong - the application detection tests are STILL satisfied:"
			$AppDetected # Show the detection report
		}
		Else
		{
			"Application detection tests satisfied - application is now installed"
		}
	}
	Else # App detected as NOT installed...
	{
		If ($ScriptType -eq "Install")
		{
			""
			"Something went wrong - the application detection tests are not satisfied:"
			& "$ScriptPath\DetectScript.ps1" $PackageOption -VerboseMode # Show the detection report
		}		
		Else
		{
			"Application detection tests unsatisfied - application is no longer installed"
		}
	}


	""
	"$ScriptType complete."

	Stop-TranscriptPlus  # Gracefully stop logging	
	
}


########################################################################
# Uninstall-App looks for installed and registered applications matching
# the filter and launches their uninstall in a standard way.  The filter 
# will be searched against both "Name" and "IdentifyingNumber" (GUID).  
# Both MSI and Setup.exe type uninstallers are handled.
# 
# Parameters:
#	-Filter "String": (positional) The registered application(s)
#           to uninstall. Specify an app name or GUID including wildcards.
#   -Args "String":  Arguments to use when launching non-MSI uninstallers.
#         Most registered uninstallers have an "UninstallString" and 
#         a "QuietUninstallString".  The "QuietUninstallString"
#         is normally used by default to trigger an uninstall,
#         but if it is unavailable or the arguments don't meet your needs,
#         this parameter will force the use of the "UninstallString" with
#         your specified arguments.
#   -MSIArgs "String": Arguments to use when launching an MSI uninstall.
#         Normally MSI uninstalls are very standard so we apply our own
#         default set of arguments: "/Quiet /NoRestart".  Use this parameter
#         if you wish to override these defaults.
#   -Force: (switch) Force this function to run an uninstall and override
#         built-in protections.  This function can find multiple registered
#         applications that match the filter.  But we only want to run uninstalls
#         non-interactively.  So we run application uninstalls that are registered
#         as MSI or have a "QuietUninstallString".  If these conditions aren't met,
#         we normally just present a message and do nothing.
#         But if there is an "UninstallString" available that you wish to run
#         as is without providing any additional arguments, use this -force
#         parameter.
#   -WhatIf: (switch) This switch provides an opportunity to test all the elements
#         of a call to Uninstall-App.  If this switch is set, no uninstallation 
#         will be performed, but the information will be presented as if it had been.
#         This was necessary due to the complexity of the inputs and outputs.
#
# Notice that we offer different arguments for MSI uninstallers (MSIArgs) vs 
# legacy uninstall.exe uninstallers (Args).  This is done because some products
# like Google Chrome have used both unistall methods.  We need to be able to use
# the appropriate arguments for whatever uninstall type we encounter.
#
# If the application you wish to uninstall is non-standard, you will
# need to use your own uninstall details - just bypass Uninstall-App, but
# take lessons from it so you can leverage useful features.
########################################################################
Function Uninstall-App
{
    param(
      [Parameter(Mandatory=$true)]
      [string]
      $Filter,

      [Parameter(Mandatory=$false)]
      [string]
      $Args,

      [Parameter(Mandatory=$false)]
      [string]
      $MSIArgs = "/Quiet /NoRestart",

      [Switch]
      $Force,

      [Switch]
      $WhatIf
    )

	"Checking to see if installed applications are registered that match '$Filter' ..."
	If ($WhatIf){"(Uninstall-App -WhatIf was specified, all uninstalls will be simulated and not actually occur)"}

	# This deprecated command searched Win32_Product to find a matching entry in Add/Remove Programs.
	# (The Filter uses WMI Query Language (WQL) syntax - '%' is a wildcard)
	# This command takes too long (20s) so was replaced by the Get-RegisteredApps function:
	# $MSIInfo = Get-WmiObject Win32_Product -Filter "Name Like '$Filter' OR IdentifyingNumber Like '$Filter'
	
	# This is the new registered application search (FilterName supports normal wildcards):
	$RegInfo = Get-RegisteredApps $Filter

	If ($RegInfo) # Matching registered application(s) found - Uninstall...
	{
		Foreach ($AppInfo in $RegInfo) # Handle possible multiple application matches...
		{
			""
			# Returns true or false for valide GUID: [guid]::TryParse("{f3fbabb4-bcfb-45eb-8fff-9b784fd68c38}", $([ref][guid]::Empty))

			If ($AppInfo.WindowsInstaller) # This application was registered by a Windows Installer MSI...
			{
				"$($AppInfo.DisplayName) v$($AppInfo.DisplayVersion) MSI found, uninstalling GUID: $($AppInfo.PSChildName)"
				"     Using the following parameters: $MSIArgs"

				# Although there is an UninstallString available in the info, it is often not populated with correct arguments
				# - particularly for our needs.  So we override with our own arguments:
				If (-Not $WhatIf){Start-Process "msiexec.exe" -argumentlist "/Uninstall $($AppInfo.PSChildName) $MSIArgs" -wait}
			}	
			ElseIf ($AppInfo.QuietUninstallString -or $AppInfo.UninstallString) # There is a classic uninstall.exe type available - let's use it...
			{
				If ($AppInfo.QuietUninstallString -and (-not $Args)) # Found quiet uninstall details that aren't being overridden... 
				{
					$Drop, $Uninstall, $ArgList = $AppInfo.QuietUninstallString -split '"', 3 # This splits the uninstall string into components that Start-Process can use (overcomes a bug)
					"$($AppInfo.DisplayName) v$($AppInfo.DisplayVersion) Quiet uninstall information available, uninstalling with command:"
					"     $Uninstall $ArgList"
					If (-Not $WhatIf)
					{
						If ($ArgList)
						{
							Start-Process $Uninstall -argumentlist $ArgList -wait
						}
						Else
						{
							Start-Process $Uninstall -wait
						}
					}
				}
				ElseIf ($AppInfo.UninstallString -and ($Args -or $Force)) # An UninstallString has been found that is normally ignored, but an override has been specified with either $Args or $Force
				{
					$Drop, $Uninstall, $ArgList = $AppInfo.UninstallString -split '"', 3 # This splits the uninstall string into components that Start-Process can use (overcomes a bug)
					"$($AppInfo.DisplayName) v$($AppInfo.DisplayVersion) Uninstall information available, forcing uninstall with command:"
					"     $Uninstall $Args"

					If (-Not $WhatIf)
					{
						If ($Args)
						{
							Start-Process $Uninstall -argumentlist $Args -wait
						}
						ElseIf ($ArgList)
						{
							Start-Process $Uninstall -argumentlist $ArgList -wait
						}
						Else
						{
							Start-Process $Uninstall -wait
						}
					}
				}
				Else # We've found only an UninstallString, but want to avoid using it by default in case it requires user interaction...
				{
					"$($AppInfo.DisplayName) v$($AppInfo.DisplayVersion) Uninstall information found: $($AppInfo.UninstallString)"
					"It won't be used - no override was specified by -Force or -Args parameters when calling the Uninstall-App function"
				}
			}
			Else # This application doesn't offer enough information to perform an uninstall...
			{
				"$($AppInfo.DisplayName) v$($AppInfo.DisplayVersion) found, but insufficient data to automate an uninstallation"
			}
			
			# Give things a moment to settle
			Start-Sleep -Seconds 5
		}
	}
	Else # No GUID found...
	{
		""
	    "No registered application matching the provided filter found - Nothing to uninstall."
	}
	""
}


########################################################################
# Get-RobocopyProgress function to display progress of Robocopy
# Taken from https://www.reddit.com/r/PowerShell/comments/p4l4fm/better_way_of_robocopy_writeprogress/?rdt=57369
# Modified to include Source as a parameter because of inacurate file counts
# in the code on Reddit
########################################################################
Function Get-RobocopyProgress {

param(
	[Parameter(Mandatory)]
	[String]$Source,
    [Parameter(Mandatory,ValueFromPipeline)]
    $InputObject
)

begin {
    [string]$file = " "
    [double]$percent = 0
    [double]$size = $Null
    [double]$count = (gci $source -file -fo -re).Count
    [double]$filesLeft = $count
    [double]$number = 0
	[datetime]$overallStartTime = [datetime]::now
	$fileStartTime = $Null

}

process {

    $Host.PrivateData.ProgressBackgroundColor='Cyan' 
    $Host.PrivateData.ProgressForegroundColor='Black'

    $data = $InputObject -split '\x09'

    If(![String]::IsNullOrEmpty("$($data[4])")){
        $file = $data[4] -replace '.+\\(?=(?:.(?!\\))+$)'
        $filesLeft--
        $number++
		$fileStartTime = [datetime]::Now
    }
    If(![String]::IsNullOrEmpty("$($data[0])")){
        $percent = ($data[0] -replace '%') -replace '\s'
    }
    If(![String]::IsNullOrEmpty("$($data[3])")){
        $size = $data[3]
    }
    [String]$sizeString =  switch ($size) {
        {$_ -gt 1TB -and $_ -lt 1024TB} {
            "$("{0:n2}" -f ($size / 1TB) + " TB")"
        }
        {$_ -gt 1GB -and $_ -lt 1024GB} {
            "$("{0:n2}" -f ($size / 1GB) + " GB")"
        }
        {$_ -gt 1MB -and $_ -lt 1024MB} {
            "$("{0:n2}" -f ($size / 1MB) + " MB")"
        }
        {$_ -ge 1KB -and $_ -lt 1024KB} {
            "$("{0:n2}" -f ($size / 1KB) + " KB")"
        }
        {$_ -lt 1KB} {
            "$size B"
        }
    }
        # Calculate elapsed time for overall operation and current file
        $overallElapsedTime = [datetime]::Now - $overallStartTime
        $fileElapsedTime = if ($fileStartTime) { [datetime]::Now - $fileStartTime } else { [timespan]::Zero }

        # Format elapsed time manually
        $overallElapsedTimeFormatted = "{0:D2}:{1:D2}:{2:D2}" -f $overallElapsedTime.Hours, $overallElapsedTime.Minutes, $overallElapsedTime.Seconds
        $fileElapsedTimeFormatted = "{0:D2}:{1:D2}:{2:D2}" -f $fileElapsedTime.Hours, $fileElapsedTime.Minutes, $fileElapsedTime.Seconds

        # Display progress with elapsed times
        Write-Progress -Activity "Currently Copying: ..\$file" `
                       -CurrentOperation "Copying: $number of $count | Copied: $(($number - 1)) / $count | Files Left: $filesLeft" `
                       -Status "Size: $sizeString | Elapsed: $overallElapsedTimeFormatted | File Time: $fileElapsedTimeFormatted | Complete: $percent%" `
                       -PercentComplete $percent
    }
}

########################################################################
# Close-Process ensures that the specified process is closed.
# If it isn't, it gives the operator an opportunity to close it before 
# forcing it closed.
########################################################################
Function Close-Process($DisplayName, $Process)
{
	""
	"Ensuring that $Displayname ($Process) is not currently running..."

	$ProcessOpen = Get-Process $Process -ErrorAction SilentlyContinue # Look for a matching running process
	if ($ProcessOpen) # Found an open process that needs closing
	{
		# try gracefully first
		"  Found running process '$Process' - attempting to close..."

		Add-Type -AssemblyName System.Windows.Forms
		Add-Type -AssemblyName System.Drawing
		$script:NewForm = New-Object System.Windows.Forms.Form
		# $NewFont = New-Object System.Drawing.Font("Constantia",18,[System.Drawing.FontStyle]::Bold)
		$script:NewForm.Font = $NewFont
		$script:NewForm.width = 600
		$script:NewForm.Height = 200
		$script:NewForm.Text = "Running $($Process.Displayname) process found"
		$script:NewLabel = New-Object System.Windows.Forms.Label
		$script:NewLabel.AutoSize = $True
		$script:NewForm.Controls.Add($script:NewLabel)
		$script:NewTimer = New-Object System.Windows.Forms.Timer
		$script:NewTimer.Interval = 1000
		$script:CountDown = 30
		
		$script:NewTimer.add_Tick(
			{
				$script:NewLabel.Text = "Please save and close $Displayname.`r`n This installer will forcefully exit $Displayname in $CountDown seconds"
				$script:CountDown--
				
				if (($ProcessOpen.HasExited -notcontains $False) -or ($script:CountDown -lt 0))
				{
					$script:NewForm.Close()
				}
			}
		)
		
		$script:NewTimer.Start()
		$script:NewForm.ShowDialog()

		if ($ProcessOpen.HasExited -notcontains $False)
		{
			"  The user closed $Displayname themselves"
		}
		else 
		{
			"  The user didn't close $Displayname in time - forcing $Displayname closed..."
		}

		$ProcessOpen.CloseMainWindow()|out-null

		if ($ProcessOpen.HasExited -contains $False) 
		{
			Stop-Process $ProcessOpen -Force |out-null
		}
	}
	Remove-Variable ProcessOpen
}

function Get-TaskSequenceContext {
    [CmdletBinding()]
    param ()

    $vars = @{
        TaskSequenceID  = $null
        SMSTSLogPath    = $null
        DeploymentType  = $null
        InTaskSequence  = $false
    }

    try {
        $tsEnv = New-Object -ComObject "Microsoft.SMS.TSEnvironment" -ErrorAction Stop
        $vars.InTaskSequence = $true

        foreach ($var in @("TaskSequenceID", "SMSTSLogPath", "DeploymentType")) {
            try {
                $value = $tsEnv.Value($var)
                if ($value) { $vars[$var] = $value }
            } catch {
                Write-Verbose "Variable $var not found in environment."
            }
        }

        Write-Verbose "Detected Task Sequence environment. TaskSequenceID: $($vars.TaskSequenceID)"
    } catch {
        Write-Verbose "Not running inside a Task Sequence (COM object unavailable)."
    }

    return [pscustomobject]$vars
}


########################################################################
# Main - Setup variables to be used by various calling scripts...


$VerbosePreference = "SilentlyContinue"
$Global:Abort = $False # This variable is needed to tell the calling program whether an Exit is required

# Disable Zone checking to prevent warnings when running executables
$env:SEE_MASK_NOZONECHECKS = 1

$objShell = New-Object -ComObject WScript.Shell # Open a WScript shell for future use

trap { Trap-Status "Unexpected error occurred: " $_ $_.FullyQualifiedErrorId} # Trap errors

# Path of shell context that is running script:
$CurrentPath = Get-Location # (Use this for writing files so they are written to where the user is launching from.)

# This returns the directory that the script is being run from.
$ScriptPath = $PSScriptRoot # (Use this to find resources packaged with the script.)

# This will check if we are running within a task sequence (SCCM or MDT)
$tsContext = Get-TaskSequenceContext

# We can use the $ScriptPath to figure out what process launched this script (important for the tag file and troubleshooting...
# First assume the script was launched manually
$LaunchProcessType = "Manual" # Used for code branching and for text to add to tag filename
$LaunchProcessMsg = "launched manually by an operator" # Text to inject into the parameter message below

if ($tsContext.InTaskSequence) { # This script was launched within a task sequence (assumes MDT)
    $LaunchProcessType = "MDT"
    $LaunchProcessMsg = "deployed by MDT via $($tsContext.TaskSequenceID)"
} else {
    "Not running in a task sequence."
}

If ($ScriptPath.contains("\ccmcache\")) # The script was launched by SCCM (this is the cache area it uses)
{
	$LaunchProcessType = "SCCM"
	$LaunchProcessMsg = "deployed by SCCM"
}
If ($ScriptPath.contains("\IMECache\")) # The script was launched by Intune (this is the cache area it uses)
{
	$LaunchProcessType = "Intune"
	$LaunchProcessMsg = "deployed by Intune"
}

If (-Not $ScriptType) # No $ScriptType was passed to Appease, so let's assume it was run from the console...
{
	$ScriptType = "Console" # By forcing a $ScriptType, we have the opportunity to possibly add some behaviours to support this mode specifically
}

# Load the configuration details from the json file:
if (-not (Test-Path "$ScriptPath\Config.json")) { Trap-Status "Error: Could not find $ScriptPath\Config.json file. It's impossible to continue." $True}
$Config = convertfrom-json $(get-content -raw -path $ScriptPath\Config.json)
$Script = $Config.Script.$ScriptType

# Tag paths:

$TagFolder = "{{TagFolder}}"
if ($TagFolder -like '*{{*') { $TagFolder = 'C:\Wrapp\Tag' } # Unexpanded template (bundle created outside Wrapp) - use the neutral default
$ResultsPath = "$TagFolder\DetectionResults.json" # File that stores detection results

# Get the logging started...
If ($Script.Tag) # If we have a valid tag...
{
	$LogFile = "$TagFolder\$($Script.Tag)_$LaunchProcessType.TFR"
}
else # We don't have a tag to use...
{
	$LogFile = "$TagFolder\UnknownScriptType_$ScriptType_$LaunchProcessType.TFR"
}
# Only certain hosts support Transcripts...
$Transcript = $True
if ($Host.Name -eq "Windows PowerShell ISE Host") {$Transcript = $False}

# Log all output to a file:
If ($Transcript)
{Write-Host
	Stop-TranscriptPlus # First let's cleanly close any transcript that may already be running
	""
	"Starting a new transcript to tag file: $LogFile"
	"Note: If an error occurs here:"
	"           Perhaps a transcript was already running (try again)"
	"           Perhaps a previous tag file with the same name was created as admin (delete it)"
	""
	$Result = Start-Transcript -Path $LogFile -Force # Now start the new transcript
}

# Setup the look of the console...
If ($Script.BackgroundColor){[console]::BackgroundColor = $Script.BackgroundColor}
If ($Script.ForegroundColor){[console]::ForegroundColor = $Script.ForegroundColor}
If ($ScriptType -ne "Console") 
{
    $desiredWidth = 100
    $desiredHeight = 30
    $bufferSize = $host.UI.RawUI.BufferSize

    # Adjust dimensions to fit within buffer size
    $windowWidth = [math]::Min($desiredWidth, $bufferSize.Width)
    $windowHeight = [math]::Min($desiredHeight, $bufferSize.Height)

    # If desired width greater than buffer, increase buffer size
    if ($desiredWidth -gt $bufferSize.Width) {
        $host.UI.RawUI.BufferSize = New-Object System.Management.Automation.Host.Size($desiredWidth, $bufferSize.Height)
    }

    # Set WindowSize
    $host.UI.RawUI.WindowSize = New-Object System.Management.Automation.Host.Size($windowWidth, $windowHeight)
	cls
	# "Height: " + $host.ui.rawui.WindowSize.Height
	# "Width:  " + $host.ui.rawui.WindowSize.Width
}

# Start interacting with the user...

If ($ScriptType -eq "Console") # We can go into a verbose mode to provide debug info!
{
	$VerboseMode = $true
	"`$VerboseMode - Extra debug information may be presented during your session"
	""
}

"Logfile: $Logfile"
""

$StartTime = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
"$ScriptType v$ScriptVersion Script for $($Script.tag) started at $StartTime"
"Using Appease v$AppeaseVersion"
""

# Report on the parameters passed to the script:
If ($CallScriptParams.Count -gt 0) 
{
  "Script $LaunchProcessMsg with the following parameters specified:"
  $CallScriptParams | fl
}
else {"Script $LaunchProcessMsg without any parameters."}
""

"Current folder: $CurrentPath"
"$($ScriptType)Script located in: $ScriptPath"

$PackagePath = $(Get-Item "FileSystem::$ScriptPath").Parent.FullName # The parent folder that houses the entire application package
"Application package folder: $PackagePath"
""

# Provide computer details...

# First the computer's distinguished name...
Try
{
    $objDomain = New-Object System.DirectoryServices.DirectoryEntry  
    $strFilter = "(&(objectCategory=computer)(name=" + $env:ComputerName + "))"

    $objSearcher = New-Object System.DirectoryServices.DirectorySearcher  
    $objSearcher.SearchRoot = $objDomain  
    $objSearcher.Filter = $strFilter

    $DN = $objSearcher.FindOne().Path

    "Computer DN: $DN"

    # The user domain isn't available when script is running within SCCM - Let's construct from the Computer DN...
    # $CurrDomain = $env:USERDNSDOMAIN #CurrentDomain
    $CurrDomain = $DN.Substring($DN.IndexOf(",DC=") + 4).Replace(",DC=", ".").ToUpper()


}
Catch # No computer DN found - not domain joined?
{
    "Computer Name: $($env:ComputerName)"
    $CurrDomain = "NoDomain"
    "This computer is not domain joined"
}
"Current Domain: $CurrDomain"
""

$OSInfo = Get-CimInstance Win32_OperatingSystem
"Detected OS: $($OSInfo.Caption)"
"Detected OS Version: $($OSInfo.Version)"
"Detected OS Build: $($OSInfo.BuildNumber)"
"Detected OS Architecture: $($OSInfo.OSArchitecture)"

# Get the OS version and Language...
"Detected OS Language: $PSUICulture"
$DisplayLanguage = (Get-WinUserLanguageList)[0].LocalizedName
"Detected Display Language: $DisplayLanguage"
""

# Load the configuration details from the json file again (so we can report any errors that may be present):
$Config = convertfrom-json $(get-content -raw -path $ScriptPath\Config.json)

# Verify that there is domain-specific information in the config.json - complain if the information is missing...
If (-Not $Config.Domain.$CurrDomain) # No domain information found.
{
    "Warning - the config.json contains no domain information for: $CurrDomain"
	""
	# Let's see if we can find domain information that the user might have rights to despite not being domain joined.  Perhaps we can use that as a substitute...

	Foreach ($TestDomain in $Config.Domain.PSObject.Properties)
	{
		# Most domains have an accessible Tag folder...
		If ($TestDomain.Value.TagFolder) # We have a tag folder to test...
		{
			If (Test-Path $TestDomain.Value.TagFolder) # This user has access to this tag folder - let's use this domain information...
			{
				$CurrDomain = $TestDomain.Name
				"The user account appears to have access to shares in the $CurrDomain domain - substituting that domain information"
				break
			}
		}
	}
}
""

# Get the domain-specific information from config.json
If ($Domain = $Config.Domain.$CurrDomain) # Domain information found
{
    "Config.json details for this specific domain:"
    $Domain | Format-List -Force | Out-String
}
""

"Current Script Type: $ScriptType"
If (-Not (($Script = $Config.Script.$ScriptType) -or ($ScriptType -eq "Console"))) # Report error if non-console $ScriptType couldn't find associated script details...
{
	Trap-Status "Could not find critical information for this script type in config.json" $True
} 
"Config.json details for this specific script type:"
If ($Script) # There are script details to show...
{
	$Script | Format-List -Force | Out-String
}
Else # It should only be "Console" $ScriptType that should use this block...
{
	"<None>"
}
""

"Config.json details (Application):"
$App = $Config.App
$App | Format-List -Force | Out-String
""

# Change the window title to reflect the installation being performed.
$host.ui.rawui.WindowTitle = "$($ScriptType) for $($App.Company) $($App.Name) v$($App.DotVersion) ($($App.Language))"

# Get current user context
$CurrentUser = New-Object Security.Principal.WindowsPrincipal $([Security.Principal.WindowsIdentity]::GetCurrent())

# Check if user running the script is running elevated as Administrator
if($CurrentUser.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator))
{
  "Running with elevated Administrator privileges"
  $host.ui.rawui.WindowTitle = "Administrator: $($host.ui.rawui.WindowTitle)" # Be consistent with normal elevated consoles by putting "Administrator" in the titlebar
}
else
{
  "Running with unelevated privileges"

  If ($Script.RunAsAdmin) # The script needs to be run elevated as administrator...
  {

	# Check if the current user is a member of a local administrator group and can be elevated to admin
	# If not, we may need to cache package files located on the network...

	$UseCache = $False # Assume the current user can be a local admin and NO package cache is required
	$UseScriptPath = $CallScriptPath # Assume the scriptpath to be used will be the one that was originally called
	$CachePackagePath = "$TagFolder\PackageCache\$($Script.tag)" # The path to store a cached copy of the package if required

	# Network mapped drive paths need to be turned into UNC paths to protect against cases where elevation disassociates from drive letters...
	$OnNetwork = $true # Let's assume the package is located on a network share

	$Drive = $(resolve-path $UseScriptPath).Drive # Identify if the script path resolves to a drive letter
	If ($Drive) # There is a drive letter 
	{
		If ($LogicalDisk = Get-CimInstance -ClassName Win32_LogicalDisk -filter "DeviceID = '$($Drive):'") # We found details of the drive we can work with...
		{
			If ($LogicalDisk.DriveType -eq 4) # The drive is a network drive...
			{
				$UseScriptPath = $UseScriptPath.Replace($LogicalDisk.DeviceID, $LogicalDisk.ProviderName)
				"Converting the script path to a UNC path: $UseScriptPath"
			}
			else # The drive is a local drive
			{
				$OnNetwork = $false 
			}
		}
	}

	If (-Not (whoami /groups /fo csv | ConvertFrom-Csv | Where-Object { $_.SID -eq "S-1-5-32-544" })) # User can't be a local admin...
	{
		# We need to prepare for the situation where the current user will be elevated to a different user...

		
		If ($OnNetwork) # We are going to need to cache the package files locally...
		{

			$UseCache = $True # We need to cache files locally!
			""
			"This user account can't be elevated to admin - different credentials will have to be provided"
			"The admin account likely won't have access to the package path: $PackagePath"

			$CacheScriptPath = $CallScriptPath.Replace($PackagePath, $CachePackagePath) # Ignore the previous $UseScriptPath we modified and do substitutions aginst the original paths.
			"Caching package locally: $CachePackagePath"
			""
			Robocopy "$PackagePath" "$CachePackagePath" /E /NJH /IS /NJS /NDL /NC /BYTES | Get-RobocopyProgress -Source $PackagePath
			#Copy-Item $PackagePath -Destination $CachePackagePath -Force -Recurse
			$UseScriptPath = $CacheScriptPath # Change the scriptpath to be used to the one that was just cached
		}
  	}

    "Elevated Administrator privileges required - starting a new elevated prompt..."
    "(This window will close when the new elevated window closes)"
    ""
    #Create a new Elevated process to Start PowerShell
    $ElevatedProcess = New-Object System.Diagnostics.ProcessStartInfo "PowerShell";

    # Pass all the arguments that called the main script to the new script call, but replace the relative path with a full path...
    # (This will preserve any behaviours such as noexit, but won't break when runas causes the current directory to move to system32)
    Foreach ($Argument in $CallScriptArguments) # Process each argument that called the main script
    {
      Switch -regex ($Argument) # Use regex comparison
      {
        "powershell.exe$" {} # Drop the powershell exe - it's not a parameter, but instead the main process being called
        "^&" {$ElevatedProcess.Arguments += "& '$UseScriptPath' "} # This will be the path that needs to have the new full path
        default {$ElevatedProcess.Arguments += "$Argument "} # For every other parameter, we just pass them through using a space seperator
      }
    }

    # Set the Process to elevated
    $ElevatedProcess.Verb = "runas"

    # Before starting the new process, stop the current transcript and rename so it is preserved
    If ($Transcript)
    {
	  $NewFile = "$TagFolder\$($Script.Tag)-Unelevated_$LaunchProcessType.TFR"

      "Renaming log file to $NewFile"
      Stop-TranscriptPlus
      Move-Item $LogFile $NewFile -force
    }

    # Minimize this console window - we don't want to see it while the new window is open...

    # This bit of code does the heavy lifting:
    Add-Type -Name Window -Namespace Console -MemberDefinition '
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, Int32 nCmdShow);
    '

    # Get a handle for this current window
    $CurrWindow = (Get-Process -Id $pid).MainWindowHandle

    # Minimize the current window
    $Result = [Console.Window]::ShowWindow($CurrWindow, 2) # A '2' minimizes the window


    #Start the new elevated process
    Try  # We need to handle any failure to launch the new window
    {
		$NewProcess = [System.Diagnostics.Process]::Start($ElevatedProcess)

		#Wait for the new elevated window to close
		$NewProcess.WaitForExit()
	}
    catch # Something went wrong - bring the original window back up so we can report it...
    {
          $Result = [Console.Window]::ShowWindow($CurrWindow, 9) # A '9' restores a minimized window
          # Log error to a new file (we renamed the old one):
          If ($Transcript)
          {
			""
			"Starting a new transcript to tag file: $LogFile"
			"Note: If an error occurs here:"
			"           Perhaps a transcript was already running (try again)"
			"           Perhaps a previous tag file with the same name was created as admin (delete it)"
			""
			  
            $Result = Start-Transcript -Path $LogFile -Force
          }
          Trap-Status "Failed to launch elevated prompt - processing cannot continue: " $_
    }

	# Processing should now have returned to the initial, unelevated process...

	If ($UseCache) # There is a cached package that needs to be cleaned up...
	{
		"Deleting the temporary cache files" 
		Remove-Item $CachePackagePath -Force -Recurse
	}

    #Close and exit from the current, unelevated, process
    [Console.Window]::ShowWindow($CurrWindow, 0) # A '0' closes the window
	$Global:Abort = $True # This variable is needed to tell the calling program that an Exit is required
    Exit
  }
}

########################################################################
# Action section

# The environment has now been built with proper admin elevation and all required variables.
# This is a good spot to add any generic code.  This section will also only be run one
# time at the proper elevation (earlier areas could be run multiple times at each elevation).

If ($ScriptType -eq "Console") # An operator has launched Appease directly without the use of a calling script - let's be welcoming...
{
    $OldForeColor = [console]::ForegroundColor
	""
	""
	"Hello $($env:UserName),"
	""
	"You are receiving this message because it appears you ran Appease directly from a console and not from another calling script."
	"If this is not the case, please ensure your calling script identifies itself and has a corresponding section in the Config.json."
	"Identify the calling script type with a line like this: `$ScriptType = ""NewScriptType"""
	""
	"If you are here because you are doing package development work, here's some advice and handy commands for you:"
	""
	"    Transcript logging started for you as soon as you launched Appease."
	"    The log file will contain the output from all your upcoming commands:"
    [console]::ForegroundColor = "Yellow"
	"		$Logfile"
    [console]::ForegroundColor = $OldForeColor	
	"    To gracefully close the log file when you are done, use: "
    [console]::ForegroundColor = "Yellow"
	"		Stop-TranscriptPlus"
    [console]::ForegroundColor = $OldForeColor	
	""
	"    Running Appease on various test platforms will allow you to see what environment details are captured."
	"    This can help you to ensure that your upcoming package supports all possible configurations."
	"    To see what variables have been made available by Appease, run the following command:"
    [console]::ForegroundColor = "Yellow"
	"		get-variable"
    [console]::ForegroundColor = $OldForeColor	
	"    To review available environment variables, run the following command:"
    [console]::ForegroundColor = "Yellow"
	"		Get-ChildItem Env:*  | Sort-Object Name"
    [console]::ForegroundColor = $OldForeColor	
	"    To include variables in strings, wrap them with braces so that even names with brackets will work - i.e.:"
    [console]::ForegroundColor = "Yellow"
	"       ""Program Files folder is: $`{env:Programfiles(x86)}""" # The tick mark here causes the syntax to be displayed to the operator without interpreting the variable  :-)
    [console]::ForegroundColor = $OldForeColor	
	""
	"    If you make changes to the Config.json during testing, you will need to re-import Appease to see the changes:"
    [console]::ForegroundColor = "Yellow"
	"		. .\Appease.ps1"
    [console]::ForegroundColor = $OldForeColor	
	""
	"    There is a snapshot feature that allows you to collect system information before and after an installation."
	"    The resulting change report can tell you about all the major system changes you can use to target a "
	"    detection or uninstallation:"
    $OldForeColor = [console]::ForegroundColor
    [console]::ForegroundColor = "Yellow"
	"		`$Before = Get-SystemSnapshot"
	"		Report-Changes `$Before"
    [console]::ForegroundColor = $OldForeColor	
	"       (The VersionInfo.xxx file properties and values can be used by Detect Tests in the config.json)"
	""
	"    To test a complex Uninstall-App filter or to safely review exactly what actions Uninstall-App "
	"    will perform without any changes occurring, use the -WhatIf parameter:"
    $OldForeColor = [console]::ForegroundColor
    [console]::ForegroundColor = "Yellow"
	"		Uninstall-App ""Microsoft Power*BI Desktop (*) (*)"" -WhatIf"
    [console]::ForegroundColor = $OldForeColor	
	""
	"    If during testing you get an error stating you can't load a file because it wasn't digitally signed,"
	"    you may be using files that have been moved between domains and which have been marked as being untrusted."
	"    Simply run the following function to remove the troublesome file property:"
    $OldForeColor = [console]::ForegroundColor
    [console]::ForegroundColor = "Yellow"
	"		Unblock-Package"  
    [console]::ForegroundColor = $OldForeColor	
	""
	
}
Else # It's not a console...
{
	# Let's capture the current state of the registered applications for future comparison:
	$Before = Get-SystemSnapshot -Apps -Services
		
	# Always run the DetectScript before doing anything (it can never be run too often)...
	$AppDetected = & "$ScriptPath\DetectScript.ps1" $PackageOption
	
	
	# If the calling script wants to enforce app detection, do that work here...
	If ($Script.DetectApp)
	{
		If ($AppDetected) # App detected as installed...
		{
			If ($ScriptType -eq "Install") # Only perform an install if the application ISN'T already installed...
			{
				""
				$AppDetected
				""
				Trap-Status "This application is detected as already being installed.`r`n  The ""DetectApp"" property prevents this installation from proceeding." $True
			}
		}
		Else # App isn't detected...
		{
			If ($ScriptType -eq "UnInstall") # Only perform an uninstall if the application IS already installed...
			{
				""
				& "$ScriptPath\DetectScript.ps1" $PackageOption -VerboseMode # Since there were no results, let's show results by using VerboseMode
				""
				Trap-Status "This application is not detected as being installed.`r`n  The ""DetectApp"" property prevents this uninstall from proceeding." $True
			}
		}
	}
}





If ($ScriptType -eq "Install") # There is preprocessing work to perform only for Installations...
{

	If ($Script.UninstallFirst) # The uninstall is to be run before continuing...
	{
		"'UninstallFirst' indicates that the uninstall must be performed before installation can continue."
		"Launching Uninstall.."
		""
		Start-Process Powershell $ScriptPath\UninstallScript.ps1 -wait

		"Uninstall should have completed now"
		""
	}
	
	# Check for the presence of any required dependencies...
	If ($App.Dependencies) # There are dependecies to process...
	{
		""
		"There are application dependencies defined that need to be verified..."
		# Get the existing Detection Results JSON so that new results can be added...
		If (Test-Path $ResultsPath) # If the results file exists, load the data into the hash...
		{
			$ResultsJSON = convertfrom-json $(get-content -raw -path $ResultsPath)

			Foreach ($Dep in $App.Dependencies) # Cycle through any dependencies that have been defined in the config.json...
			{
				If ($ResultsJSON.$($Dep.AppName).DetectionSatisfied) # The Dependency AppName was found in the DetectionResults and was successful!
				{
					If ($ResultsJSON.$($Dep.AppName).DotVersion -ge $Dep.AppDotVersion)
					{
						"  $($ResultsJSON.$($Dep.AppName).Company) $($Dep.AppName) v$($ResultsJSON.$($Dep.AppName).DotVersion) is already installed - satisfying a dependency."
					}
					Else
					{
						Trap-Status "  This application requires that $($ResultsJSON.$($Dep.AppName).Company) $($Dep.AppName) v$($Dep.AppDotVersion) be installed previously; `r`n  however, only v$($ResultsJSON.$($Dep.AppName).DotVersion) has been detected - processing cannot continue." $True
					}	
				}
				else # The Dependency wasn't satisfied...
				{
					Trap-Status "  This application requires that $($ResultsJSON.$($Dep.AppName).Company) $($Dep.AppName) be installed previously.`r`n  It has not been detected - processing cannot continue." $True
				}
			}	
			""
			"All application dependencies are satisfied.  Installation can continue."
		}
		else
		{
			Trap-Status "  The file $ResultsPath that contains the detection results could not be found! - processing cannot continue." $True
		}
	}
}


# Config.json defines what processes are associated with this application - let's process what it says...

If ($Script.CloseRunning) # Running processes are to be closed before continuing...
{
	Foreach ($DR in $App.DetectRunning) # Examine each process defined... 
	{
		Close-Process $DR.DisplayName $DR.Process
	}
}

