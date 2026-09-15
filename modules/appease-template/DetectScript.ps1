########################################################################
# Welcome to DetectScript written by Gordon P. Martin
# Version 2.4-Generic
#
# DetectScript is a much simpler script than normal because it is meant
# to be imported into an SCCM application object and gets executed
# multiple times a day on each and every SCCM client computer. There is
# limited messaging due to the limits of SCCM detection scripts,
# but details of a detection are stored in an aggregate json file on the PC:
# 	C:\Contoso\Tag\DetectionResults.json
#
# It is important to note that if you want SCCM to trigger an install,
# this script must return a null (exit 0) result.  Only if an install
# is not to happen can output be generated.
#
# This script can use variables in the Config.json file, but only when
# executed from outside SCCM.  When the SCCMPackager imports this 
# script into SCCM, it also injects the config.json data as code within
# the imported script. (It's a bit weird, but makes things easy to manage.)
#
# This particular version of the script has not been customized for a specific
# application.  It is relying on the generic information pulled from the 
# Config.json|Script|Detect section to know what file version to test for.
# More complicated tests will require custom code to be added to the bottom
# of this script.
#
# Parameters:
# "-VerboseMode" parameter can be used to output all test results; however,
#     VerboseMode will break true/false detection.
#
########################################################################
# v2.4 - 2026-04-15
#      - Added a check for $detProperty to screen for "Exists". For filepaths
#        this could work but for registry, it contains no such property.
#        This is a common scenario and caused a lot of confusion. Added it for simplicity
#        via a Test-Path boolean result when the $detProperty is Exists to prevent reg issue.
# v2.3 - 2025-07-15
#      - Introduced $DetCommand support in Detect-ObjectProperty, allowing use of any 
#        built-in or custom PowerShell cmdlet/function as a detection source, instead 
#        of being limited to file or registry Get-ItemProperty calls.
#      - Integrated [AUVersion] (custom class!) comparison logic to properly handle semantic versioning.
#        This ensures accurate comparisons like "1.0.1" vs "1.0.1.0", where [version] 
#        alone would treat the former as *less than* due to missing build fields 
#        being interpreted as -1.
#      - Added fallback to [version] casting if [AUVersion] comparison fails, providing 
#        broad compatibility across all property types and value formats.
#      - Refactored Detect-ObjectProperty to condense logic, reducing line count.
# v2.2 - 2025-02-25 by Gordon Martin
#      - Fixed Version compare (recent changes had made it a string compare)
# v2.1 - 2025-02-14 by Gordon Martin
#      - Added code to ensure that the C:\Contoso\Tag exists (some servers don't have it)
# v2.0 - 2024-12-12 by Gordon Martin
#      - Added support for multiple Detection Expressions that can be
#        specified by the InstallScript, UninstallScript, IntunePackager or SCCMPackager.
# v1.9 - 2024-12-04 by Gordon Martin
#      - Added a VerboseMode to support better testing an reporting
# v1.8 - 2024-10-22 by Gordon Martin
#      - Changed DetectionResults.json output to report against main
#        App.Name with properties of App.Company and App.DotVersion.
#        This will allow other apps with a dependency to be able to 
#        retrieve detection results regardless of version.
#      - Added Console testing support with a VerboseMode.
# v1.7 - 2024-10-08
#      - Changed Store-Results to use SID
# v1.6 - 2024-07-16 - Trevor Ritchie
#	   - Added support for French OS
# v1.5 - 2024-07-09 by Gordon Martin
#      - Changed Detect-ObjectProperty to return test results rather than
#        processing results itself. (Offers more logic flexibility.)
#      - Created Evaluate-Expression function to evaluate logical
#        expressions provided by Config.json.
# v1.4 - 2024-07-05 by Gordon Martin
#      - Implemented support for multiple tests - nesting them under
#        one application entry.  This required changes to the Config.json fomat as well.
#      - Added support for non-version value types ([string] vs [version]).
#      - Implemented comparison operator coming from Config.json - this allows 
#        for more sophisticated tests to be performed.
# v1.3 - 2024-06-05 by Gordon Martin
#      - Shortened $ResultsHash.$ResultsEntry to $Entry to simplify coding
#        for authors that must make custom code.
# v1.2 - April 12, 2024 by Gordon Martin
#      - Added support for generic detection parameters in the Config.json
#      - For a normal test like testing a file version, all changes can 
#        be made entirely in the Detect section of the json file.
# v1.1 - April 8, 2024 by Gordon Martin
#      - Added logging to Tag\DetectionResults.json
#      - File is stored on each PC and contains detection results for
#        all applications assigned to the PC that use DetectScript v1.1 or greater
# v1.0 - November 28, 2023 by Gordon Martin
#      - Initial release
########################################################################
param ([string]$PackageOption = "Default", [switch]$VerboseMode = $False) # Parameters here are used to alter script behaviour
# The $PackageOption parameter can be used by calling scripts to pass any PackageOption being used in order to target a specific Detect Expression.

$CalledByScript = $ScriptType # Capture the scripttype that called this script so behaviour can be altered appropriately
$ScriptType = "Detect"
$ScriptVersion = "2.4"

$TagFolder = "{{TagFolder}}"
if ($TagFolder -like '*{{*') { $TagFolder = 'C:\Wrapp\Tag' } # Unexpanded template (bundle created outside Wrapp) - use the neutral default
$ResultsPath = "$TagFolder\DetectionResults.json" # File that logs the detection

# This is where Config.json data is injected into the code when importing into SCCM:
# (The $DetectExpression is also injected because SCCM doesn't pass parameters to the DetectScript when detecting.)

# Inject Config.json data here

$DetectExpression = "Expression_$PackageOption" # The Detect Expression we expect to use.


# If the Config.json data wasn't injected above, load the configuration details from the json file:
if (-not $Config) 
{
    # This returns the directory that the script is being run from.
    $ScriptPath = Split-Path (Get-Variable MyInvocation -Scope 0).Value.MyCommand.Path # (Use this to find the config.json file.)

    $Config = convertfrom-json $(get-content -raw -path $ScriptPath\Config.json)
}

If ($CalledByScript -eq "Console") {$VerboseMode = $true} # If someone called this script from a console, we can go into a verbose mode to provide testing results!

$App = $Config.App
$Script = $Config.Script.$ScriptType

# Time to check if we have a valid $DetectExpression...

If (-Not $Script.$($DetectExpression)) # Coudln't find a matching expression...
{
	If ($Script.Expression_Default) # A default is available to use instead...
	{
		If ($VerboseMode) 
		{
			"A Detect Expression ""$DetectExpression"" could not be found to match the Package Option ""$PackageOption"""
			"Using a default Detect Expression instead: Expression_Default"
		}
		$DetectExpression = "Expression_Default"
	}
	else # We couldn't find a useable alternative...
	{
		Exit "Error: Processing could not continue because no useable Detect Expression could be found.`r`n  - In the config.json, create a ""$DetectExpression"" or ""Expression_Default"" entry."
	}
}
$Expression = $Script.$($DetectExpression) # The expression string to be used for evaluation.

# Get the existing Results JSON so that new results can be added...
$ResultsHash = [ordered] @{} # Hash to house the Results data
If (Test-Path $ResultsPath) # If the results file exists, load the data into the hash...
{
	$ResultsJSON = get-content -raw -path $ResultsPath
	(convertfrom-json $ResultsJSON).psobject.properties | Sort-Object Name | Foreach { $ResultsHash[$_.Name] = $_.Value }
}
ElseIf (-Not (Test-Path $TagFolder)) # ensure the parent folder also exists...
{
    
    New-Item -ItemType Directory -Path $TagFolder -Force
}


# Add an entry for the current application:
$ResultsEntry = $App.Name
If ($PackageOption -ne "Default"){$ResultsEntry += "_$PackageOption"} # Include the package option in the name if it is using soemthing other than Default.
$ResultsHash.$ResultsEntry = @{DetectDate = $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")} # The hash wrapper creates a sub-hash that the remaining variables also use
$Entry = $ResultsHash.$ResultsEntry
$Entry.Company = $App.Company
$Entry.DotVersion = $App.DotVersion

If ($VerboseMode) {"Detection Results for $($ResultsEntry) v$($Entry.DotVersion) by $($Entry.Company):"} # Report tested app details 

$Entry.DetectionSatisfied = $true # Assume detection is satisfied.
$Entry.Tests = @{} # Create a subsection for test results

$TestMessages = "" # A variable is needed to report possible failed test results.

########################################################################
# AUVersion Class Definition
# Version: 1.1
#
# Purpose:
#   Provides a SemVer-compatible versioning system to replace or
#   supplement the built-in [version] type in PowerShell. This class
#   handles both 3- and 4-part version strings, as well as optional
#   prerelease and build metadata components (e.g., 1.2.3-alpha+build42).
#
# Features:
#   - Accurate and flexible parsing using strict or lenient modes.
#   - Implements IComparable for correct version ordering.
#   - Gracefully handles version strings with differing segment lengths.
#   - Supports Semantic Versioning (SemVer 2.0) structures.
#   - Enables reliable comparison logic even with versions like:
#     * 1.1.1 vs. 1.1.0.201 (which [version] mishandles)
#     * 1.0.0-alpha vs. 1.0.0
#     * 136 vs. 136.0.0.0 → treated as equal
#
# Usage:
#   $v1 = [AUVersion]::Parse("1.1.1")
#   $v2 = [AUVersion]::Parse("1.1.0.201")
#   if ($v1.CompareTo($v2) -gt 0) { "Newer version found" }
#
#   CompareTo() Return Values:
#   e.g. $v1 compared to other version $v2
#     - 1  → This version is **newer** than the other version.
#     - 0  → This version is **equal** to the other version.
#     - -1 → This version is **older** than the other version.
#
# Notes:
#   - Versions with missing components are automatically normalized:
#       * "1.2" is treated as "1.2.0.0"
#       * "136" is treated as "136.0.0.0"
#       * "1.2.3-alpha" is treated as "1.2.3.0-alpha"
#
# Included Helpers:
#   ConvertTo-AUVersion - Shortcut function for casting strings or
#                         [version] objects to [AUVersion] safely.
#
# Placement:
#   This should be loaded before any logic that evaluates versions.
########################################################################
# v1.1 - 2025-07-15
#      - Introduced NormalizeVersion():
#        - Ensures all version inputs are padded to 4 segments (e.g., 136 → 136.0.0.0).
#        - Prevents misleading results when comparing 1-part vs 4-part versions.
#      - Updated constructors and WithVersion() to normalize versions automatically.
#      - Enhanced TryParse():
#        - Supports version strings with 0–3 dots (1 to 4 segments).
#        - Appends '.0' to single-segment versions (e.g., '1' becomes '1.0').
#        - Uses explicit `$regex.Match()` instead of relying on `$Matches` auto-assignment.
#      - Refined regex logic in GetPattern():
#        - Changed version pattern to allow `{0,3}` dots for full flexibility.
#        - Removed backtick (`) at the end of the strict-mode pattern for correctness.
#      - Additional changes:
#        - Ensured `$Matches` is reassigned explicitly to maintain correct scope.
#        - Replaced use of implicit regex match with more robust `Regex.Match()` object.
# v1.0 - 2021 by majkinetor https://github.com/majkinetor/au/blob/master/AU/Private/AUVersion.ps1
#      - Initial version: part of toolset for AU (Automatic Updater) for Chocolatey.
########################################################################
class AUVersion : System.IComparable {
    [version] $Version
    [string] $Prerelease
    [string] $BuildMetadata

    hidden static [version] NormalizeVersion([version] $v) {
        $build = if ($v.Build -ge 0) { $v.Build } else { 0 }
        $rev   = if ($v.Revision -ge 0) { $v.Revision } else { 0 }

        return [version]::new($v.Major, $v.Minor, $build, $rev)
    }

    AUVersion([version] $version, [string] $prerelease, [string] $buildMetadata) {
        if (!$version) { throw 'Version cannot be null.' }
        $this.Version = [AUVersion]::NormalizeVersion($version)
        $this.Prerelease = $prerelease
        $this.BuildMetadata = $buildMetadata
    }

    AUVersion($input) {
        if (!$input) { throw 'Input cannot be null.' }
        $v = [AUVersion]::Parse($input -as [string])
        $this.Version = $v.Version
        $this.Prerelease = $v.Prerelease
        $this.BuildMetadata = $v.BuildMetadata
    }

    static [AUVersion] Parse([string] $input) { return [AUVersion]::Parse($input, $true) }

    static [AUVersion] Parse([string] $input, [bool] $strict) {
        if (!$input) { throw 'Version cannot be null.' }
        $reference = [ref] $null
        if (![AUVersion]::TryParse($input, $reference, $strict)) { throw "Invalid version: $input." }
        return $reference.Value
    }

    static [bool] TryParse([string] $input, [ref] $result) { return [AUVersion]::TryParse($input, $result, $true) }

    static [bool] TryParse([string] $input, [ref] $result, [bool] $strict) {
    $result.Value = [AUVersion] $null
    if (!$input) { return $false }

    $pattern = [AUVersion]::GetPattern($strict)
    $regex = [regex] $pattern
    $match = $regex.Match($input)
    if (!$match.Success) { return $false }

    $Matches = $match.Groups  # Reassign for compatibility with original code structure

    $versionText = $Matches['version'].Value
    if ($versionText -notmatch '\.') {
        $versionText += '.0'
    }

    $reference = [ref] $null
    if (![version]::TryParse($versionText, $reference)) { return $false }

    $pr = $Matches['prerelease'].Value
    $bm = $Matches['buildMetadata'].Value

    if ($pr -and !$strict) { $pr = $pr.Replace(' ', '.') }
    if ($bm -and !$strict) { $bm = $bm.Replace(' ', '.') }
    if ($pr -and $strict -and $pr -like '*.*') { return $false }
    if ($bm -and $strict -and $bm -like '*.*') { return $false }
    if ($pr) { $pr = $pr.Replace('.', '') }
    if ($bm) { $bm = $bm.Replace('.', '') }

    $normalized = [AUVersion]::NormalizeVersion($reference.Value)
    $result.Value = [AUVersion]::new($normalized, $pr, $bm)
    return $true
    }

    hidden static [string] GetPattern([bool] $strict) {
        # Accept 1–4 version segments, e.g., 136, 136.0, 136.0.0, 136.0.0.0
        $versionPattern = '(?<version>\d+(?:\.\d+){0,3})'

        if ($strict) {
            $identifierPattern = "[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*"
            return "^$versionPattern(?:-(?<prerelease>$identifierPattern))?(?:\+(?<buildMetadata>$identifierPattern))?$"
        } else {
            $identifierPattern = "[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+| \d+)*"
            return "^$versionPattern(?:[- ]*(?<prerelease>$identifierPattern))?(?:[+ *](?<buildMetadata>$identifierPattern))?$"
        }
    }

    [AUVersion] WithVersion([version] $version) {
        return [AUVersion]::new([AUVersion]::NormalizeVersion($version), $this.Prerelease, $this.BuildMetadata)
    }

    [int] CompareTo($obj) {
        if ($obj -eq $null) { return 1 }
        if ($obj -isnot [AUVersion]) { throw "AUVersion expected: $($obj.GetType())" }
        $t = $this.GetParts()
        $o = $obj.GetParts()
        for ($i = 0; $i -lt $t.Length -and $i -lt $o.Length; $i++) {
            if ($t[$i].GetType() -ne $o[$i].GetType()) {
                $t[$i] = [string] $t[$i]
                $o[$i] = [string] $o[$i]
            }
            if ($t[$i] -gt $o[$i]) { return 1 }
            if ($t[$i] -lt $o[$i]) { return -1 }
        }
        if ($t.Length -eq 1 -and $o.Length -gt 1) { return 1 }
        if ($o.Length -eq 1 -and $t.Length -gt 1) { return -1 }
        if ($t.Length -gt $o.Length) { return 1 }
        if ($t.Length -lt $o.Length) { return -1 }
        return 0
    }

    [bool] Equals($obj) { return $this.CompareTo($obj) -eq 0 }

    [int] GetHashCode() { return $this.GetParts().GetHashCode() }

    [string] ToString() {
        $result = $this.Version.ToString()
        if ($this.Prerelease) { $result += "-$($this.Prerelease)" }
        if ($this.BuildMetadata) { $result += "+$($this.BuildMetadata)" }
        return $result
    }

    [string] ToString([int] $fieldCount) {
        if ($fieldCount -eq -1) { return $this.Version.ToString() }
        return $this.Version.ToString($fieldCount)
    }

    hidden [object[]] GetParts() {
        $result = @($this.Version)
        if ($this.Prerelease) {
            $this.Prerelease -split '\.' | ForEach-Object {
                if ($_ -match '^[0-9]+$') {
                    $result += [int] $_
                } else {
                    $result += $_
                }
            }
        }
        return $result
    }
}

function ConvertTo-AUVersion($Version) {
    return [AUVersion] $Version
}

########################################################################
# Store-Results writes the $ResultsHash to a json file. 
# It also sets User group permissions to avoid future problems.
########################################################################
Function Store-Results
{
	# Store the ResultsHash as a json file
	set-content -path $ResultsPath -value $(convertto-json $ResultsHash -Depth 10) # Store the results to file

	# Ensure that the local Users group has full rights to the log file...
	# This ensures that various processes don't experience an error in the future.

	$NewAcl = Get-Acl -Path $ResultsPath # Preserve existing permissions
    
	$usersGroup = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-545") # BUILTIN\Users group, so we avoid language issues
	$FSAccessRule = New-Object System.Security.AccessControl.FileSystemAccessRule ($usersGroup, "FullControl", "Allow")
    
	# Set permission
	$NewAcl.SetAccessRule($FSAccessRule)
	Set-Acl -Path $ResultsPath -AclObject $NewAcl
}


########################################################################
# Detect-ObjectProperty evaluates a target object's specified property 
# against the desired value.  The target could be a file or registry key.
########################################################################
Function Detect-ObjectProperty($DetName, $DetSymbol, $DetPath, $DetProperty, $DetOperator, $DetValue, $DetCommand = $null) 
{
	$Entry.Tests.$DetName = @{Symbol = $DetSymbol;Path = $DetPath;Command = $DetCommand;Property = $DetProperty;Operator = $DetOperator;TestValue = $DetValue;TestSatisfied = $false}
	$TestEntry = $Entry.Tests.$DetName
	$RetrievedValue = $null

	if ($DetCommand) 
	{
		try {
			$obj = Invoke-Expression $DetCommand
		}
		catch {
			$TestEntry.DetectedValue = "<error>"
			$Script:TestMessages += "  Test failed: $DetName (<error>) $_`r`n"
			return $false
		}

		if (-not $obj) {
			$TestEntry.DetectedValue = "N/A"
			$Script:TestMessages += "  Test failed: $DetName (<no object returned>)`r`n"
			return $false
		}

		$RetrievedValue = $obj.$DetProperty
	}
	else
	{
		# The path to the application object of interest
		#=======================================================================
		$TestPath = Invoke-Expression """$DetPath""" # This weird code is necessary to resolve any environemt variables that may have been utilized
		# Example: # $TestPath = "$(${Env:ProgramFiles})\Google\Chrome\Application\chrome.exe"
		#=======================================================================
		$TestEntry.Path = $TestPath

		if ($DetProperty -eq 'Exists') {
			# Existence test -- works for both filesystem and registry paths.
			# Test-Path is provider-aware (handles HKLM:\, HKCU:\, C:\, etc.)
			# whereas Get-ItemProperty does not expose an "Exists" property on
			# registry keys (its return value's properties are the value names).
			# Note: do NOT early-return on missing path here -- that would prevent
			# authoring a "must NOT exist" rule via Value = "False".
			$RetrievedValue = if (Test-Path $TestPath) { 'True' } else { 'False' }
		}
		else {
			if (-not (Test-Path $TestPath)) {
				$TestEntry.DetectedValue = "N/A"
				$Script:TestMessages += "  Test failed: $DetName (<object not found>)`r`n"
				return $false
			}

			$RetrievedValue = Invoke-Expression "(Get-ItemProperty `$TestPath).$DetProperty"
		}
	}

	$TestEntry.DetectedValue = [string]$RetrievedValue

	$auRetrievedValue = $null
	$auExpected = $null
	if ([AUVersion]::TryParse("$RetrievedValue", [ref]$auRetrievedValue) -and [AUVersion]::TryParse("$DetValue", [ref]$auExpected)) {
		$compareResult = $auRetrievedValue.CompareTo($auExpected)
		$TestEntry.TestSatisfied = Invoke-Expression "`$compareResult $DetOperator 0"
	}
	else {
		try { [version]$RetrievedValue = $RetrievedValue; [version]$DetValue = $DetValue } catch {}
		try {
			$TestEntry.TestSatisfied = Invoke-Expression "`$RetrievedValue $DetOperator `$DetValue"
		}
		catch {
			$Script:TestMessages += "  Test error: $DetName (invalid comparison expression)`r`n"
			return $false
		}
	}

	if ($TestEntry.TestSatisfied) {
		$Script:TestMessages += "  Test satisfied: $DetName ($RetrievedValue)`r`n"
	}
	else {
		$Script:TestMessages += "  Test failed: $DetName ($RetrievedValue)`r`n"
	}

	return $TestEntry.TestSatisfied
}

########################################################################
# Evaluate-Expression evaluates the various test results against the
# provided expression to see what the overall detection result should be.
########################################################################
Function Evaluate-Expression
{
	# Create the Expression subsection to house the expression details for the current detection:
	$Entry.Expression = @{}
	$Exp = $Entry.Expression
	
	If (-Not $Expression) # We don't have a proper expression to evaluate...
	{
		$Exp.Provided = "<None provided>"
		$Exp.Evaluated = "<N/A>"
		Return $false # We couldn't evaluate anything fo return false.
	}
	else # Let's process the provided expression...
	{
		$Exp.Provided = $Expression
		
		# Start replacing symbols in the provided expression with the actual test results...
		
		# Sort by symbol length descending so longer symbols get replaced before shorter ones
		# that may be substrings (e.g. "BaseV2" before "Base").
		Foreach ($TestResult in ($Entry.Tests.PSObject.Properties.Value | Sort-Object { $_.Symbol.Length } -Descending))
		{
			$Expression = $Expression.Replace($TestResult.Symbol, "`$$($TestResult.TestSatisfied)") # Substitute the symbols with the results.
		}
		$Result = Invoke-Expression $Expression # Evaluate the resulting expression as if it was a code statement (should return true or false)
		$Exp.Evaluated = "$Expression = `$$Result"
		Return $Result
	}
	
	# $TestPath = Invoke-Expression """$DetPath""" # This weird code is necessary to resolve any environemt variables that may have been utilized
}


########################################################################
# Code specific to this product...
# Please remember that if you want SCCM to trigger an install, you can't output any text and must "exit 0"

# <Code that applies to all targeted systems goes here>


# Loop through the available tests and process them...
Foreach ($Test in $Script.Tests)
{
	# Only perform a test if it is included in the expression - there can be many more tests available than we may want to run in this situation...
	
	If ($Expression.Contains($Test.Symbol)) # the symbol for this test is in the expression...
	{
		$Result = Detect-ObjectProperty $Test.Name $Test.Symbol $Test.Path $Test.Property $Test.Operator $Test.Value $Test.Command # Perform a test

		If ($VerboseMode) # Report the test results
		{
			"Test $($Test.Name):"
			$TestDetails = $Entry.Tests.$($Test.Name)
			Foreach ($Detail in $TestDetails.Keys)
			{
				"`t$Detail`t: $($TestDetails.$Detail)"
				
			}
			
			# $Entry.Tests.$($Test.Name)
			""
		}
		
		# If ($Result -eq $false) {$Entry.DetectionSatisfied = $false} # If the test failed, fail the overall detection.
	}
}


$Entry.DetectionSatisfied = Evaluate-Expression

If ($VerboseMode) # Report the expression evaluation results...
{
	"Overall results expression:"
	"`tProvided`t: $($Entry.Expression.Provided)"
	"`tEvaluated`t: $($Entry.Expression.Evaluated)"
	# $Entry.Expression
	""
	"$($Entry.Company) $($ResultsEntry) v$($Entry.DotVersion) detected: $($Entry.DetectionSatisfied)"
	""
}


# If this is the only detection required, you don't need to modify this script, but rather
# just update the Config.json Script|Detect section.

# If more complex detection is required, comment out the Detect-ObjectProperty call
# and instead add required code here.  Feel free to use the function's code as an example.


#End of code specific to install
########################################################################

# Write the results to the DetectionResults.json file:
Store-Results

If ($Entry.DetectionSatisfied -eq $false) # Detection failed one or more tests...
{
	# Exit 0 so that SCCM can trigger an install:
	exit 0
}
else # All detection tests passed - let's report the test results now since it won't interfere with an SCCM trigger...
{
	"Detection is satisfied: `r`n" + $TestMessages 
}


