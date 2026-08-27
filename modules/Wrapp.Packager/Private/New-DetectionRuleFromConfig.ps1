function New-DetectionRuleFromConfig {
    <#
    .SYNOPSIS
        Builds detection rule objects from config or via script template injection.

    .DESCRIPTION
        Supports two paths:
        1. Multi-type dispatch: When DetectionRules array is provided, builds
           MSI, File, Registry, or Script detection rules from config objects.
        2. Script template injection (backward compatible): When DetectionRules
           is absent, injects Config.json data into DetectScript.ps1 template.

    .PARAMETER DetectionRules
        Array of detection rule config objects with Type field.
        When provided, uses multi-type dispatch.

    .PARAMETER ScriptPath
        Path to the Script directory (for script-type detection rule files).

    .PARAMETER DetectScriptPath
        Path to the DetectScript.ps1 template (legacy script injection path).

    .PARAMETER App
        The App section from Config.json (legacy script injection path).

    .PARAMETER DetectConfig
        The Script.Detect section from Config.json (legacy script injection path).

    .PARAMETER PackageOption
        The package option string (legacy script injection path).

    .PARAMETER OutputDirectory
        Directory where generated detection scripts are written (legacy path).

    .PARAMETER AppName
        Application name for the output file naming (legacy path).

    .OUTPUTS
        Array of detection rule objects.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Legacy')]
    param(
        [Parameter(ParameterSetName = 'MultiType', Mandatory = $true)]
        [array]$DetectionRules,

        [Parameter(ParameterSetName = 'MultiType')]
        [string]$ScriptPath,

        [Parameter(ParameterSetName = 'Legacy', Mandatory = $true)]
        [string]$DetectScriptPath,

        [Parameter(ParameterSetName = 'Legacy', Mandatory = $true)]
        $App,

        [Parameter(ParameterSetName = 'Legacy')]
        $DetectConfig,

        [Parameter(ParameterSetName = 'Legacy')]
        [string]$PackageOption = 'Default',

        [Parameter(ParameterSetName = 'Legacy', Mandatory = $true)]
        [string]$OutputDirectory,

        [Parameter(ParameterSetName = 'Legacy', Mandatory = $true)]
        [string]$AppName
    )

    # ======================================================================
    # Multi-type dispatch path
    # ======================================================================
    if ($PSCmdlet.ParameterSetName -eq 'MultiType') {
        $rules = @()

        foreach ($dr in $DetectionRules) {
            if (-not $dr.Type) {
                Write-Log "Detection rule missing 'Type' field, skipping" -Type 2
                continue
            }

            try {
                switch ($dr.Type) {
                    'MSI' {
                        $msiParams = @{
                            ProductCode = $dr.ProductCode
                        }
                        if ($dr.ProductVersionOperator) {
                            $msiParams['ProductVersionOperator'] = $dr.ProductVersionOperator
                        }
                        if ($dr.ProductVersion) {
                            $msiParams['ProductVersion'] = $dr.ProductVersion
                        }
                        $rules += New-IntuneWin32AppDetectionRuleMSI @msiParams
                        Write-Log "Created MSI detection rule: $($dr.ProductCode)"
                    }
                    'File' {
                        $baseParams = @{
                            Path         = $dr.Path
                            FileOrFolder = $dr.FileOrFolder
                        }
                        if ($null -ne $dr.Check32BitOn64System) {
                            $baseParams['Check32BitOn64System'] = [bool]$dr.Check32BitOn64System
                        }

                        $method = if ($dr.DetectionMethod) { $dr.DetectionMethod } else { 'Existence' }
                        switch ($method) {
                            'Existence' {
                                $baseParams['Existence'] = $true
                                $baseParams['DetectionType'] = if ($dr.DetectionType) { $dr.DetectionType } else { 'exists' }
                                $rules += New-IntuneWin32AppDetectionRuleFile @baseParams
                            }
                            'DateModified' {
                                $baseParams['DateModified'] = $true
                                $baseParams['Operator'] = $dr.Operator
                                $baseParams['DateTimeValue'] = [datetime]::Parse($dr.Value)
                                $rules += New-IntuneWin32AppDetectionRuleFile @baseParams
                            }
                            'DateCreated' {
                                $baseParams['DateCreated'] = $true
                                $baseParams['Operator'] = $dr.Operator
                                $baseParams['DateTimeValue'] = [datetime]::Parse($dr.Value)
                                $rules += New-IntuneWin32AppDetectionRuleFile @baseParams
                            }
                            'Version' {
                                $baseParams['Version'] = $true
                                $baseParams['Operator'] = $dr.Operator
                                $baseParams['VersionValue'] = $dr.Value
                                $rules += New-IntuneWin32AppDetectionRuleFile @baseParams
                            }
                            'Size' {
                                $baseParams['Size'] = $true
                                $baseParams['Operator'] = $dr.Operator
                                $baseParams['SizeInMBValue'] = [string]$dr.Value
                                $rules += New-IntuneWin32AppDetectionRuleFile @baseParams
                            }
                            default {
                                Write-Log "Unknown File detection method '$method', skipping" -Type 2
                            }
                        }
                        Write-Log "Created File detection rule: $($dr.Path)\$($dr.FileOrFolder) ($method)"
                    }
                    'Registry' {
                        $baseParams = @{
                            KeyPath = $dr.KeyPath
                        }
                        if ($dr.ValueName) { $baseParams['ValueName'] = $dr.ValueName }
                        if ($null -ne $dr.Check32BitOn64System) {
                            $baseParams['Check32BitOn64System'] = [bool]$dr.Check32BitOn64System
                        }

                        $method = if ($dr.DetectionMethod) { $dr.DetectionMethod } else { 'Existence' }
                        switch ($method) {
                            'Existence' {
                                $baseParams['Existence'] = $true
                                $baseParams['DetectionType'] = if ($dr.DetectionType) { $dr.DetectionType } else { 'exists' }
                                $rules += New-IntuneWin32AppDetectionRuleRegistry @baseParams
                            }
                            'StringComparison' {
                                $baseParams['StringComparison'] = $true
                                $baseParams['StringComparisonOperator'] = $dr.Operator
                                $baseParams['StringComparisonValue'] = $dr.Value
                                $rules += New-IntuneWin32AppDetectionRuleRegistry @baseParams
                            }
                            'IntegerComparison' {
                                $baseParams['IntegerComparison'] = $true
                                $baseParams['IntegerComparisonOperator'] = $dr.Operator
                                $baseParams['IntegerComparisonValue'] = [string]$dr.Value
                                $rules += New-IntuneWin32AppDetectionRuleRegistry @baseParams
                            }
                            'VersionComparison' {
                                $baseParams['VersionComparison'] = $true
                                $baseParams['VersionComparisonOperator'] = $dr.Operator
                                $baseParams['VersionComparisonValue'] = $dr.Value
                                $rules += New-IntuneWin32AppDetectionRuleRegistry @baseParams
                            }
                            default {
                                Write-Log "Unknown Registry detection method '$method', skipping" -Type 2
                            }
                        }
                        Write-Log "Created Registry detection rule: $($dr.KeyPath) ($method)"
                    }
                    'Script' {
                        # For explicit Script detection rules, look for ScriptFile in config
                        $scriptFile = $null
                        if ($dr.ScriptFile) {
                            $scriptFile = $dr.ScriptFile
                        }
                        elseif ($ScriptPath) {
                            # Default to DetectScript.ps1 in the script directory
                            $scriptFile = Join-Path -Path $ScriptPath -ChildPath 'DetectScript.ps1'
                        }

                        if (-not $scriptFile -or -not (Test-Path -Path $scriptFile)) {
                            Write-Log "Script detection rule: ScriptFile not found, skipping" -Type 2
                            continue
                        }

                        $scriptParams = @{
                            ScriptFile = $scriptFile
                        }
                        if ($null -ne $dr.EnforceSignatureCheck) {
                            $scriptParams['EnforceSignatureCheck'] = [bool]$dr.EnforceSignatureCheck
                        }
                        if ($null -ne $dr.RunAs32Bit) {
                            $scriptParams['RunAs32Bit'] = [bool]$dr.RunAs32Bit
                        }
                        $rules += New-IntuneWin32AppDetectionRuleScript @scriptParams
                        Write-Log "Created Script detection rule: $scriptFile"
                    }
                    default {
                        Write-Log "Unknown detection rule type '$($dr.Type)', skipping" -Type 2
                    }
                }
            }
            catch {
                Write-Log "Failed to create detection rule (Type=$($dr.Type)): $_" -Type 3
            }
        }

        return $rules
    }

    # ======================================================================
    # Legacy script template injection path (backward compatible)
    # ======================================================================

    # Load the detection script template
    $DetectScript = Get-Content -Raw -Path $DetectScriptPath

    # Expand the endpoint tag-folder token for bundles whose DetectScript
    # still carries it (e.g. created outside the GUI from the raw template);
    # GUI-created bundles arrive with the path already baked in. Value comes
    # from user-defaults.json via the merged module defaults.
    if ($DetectScript.Contains('{{TagFolder}}')) {
        $DetectScript = $DetectScript.Replace('{{TagFolder}}', [string]$script:ModuleDefaults.EndpointTagFolder)
    }

    # Build the config injection block
    $ConfigString = "`$Config = @{}`r`n"

    if ($App) {
        $ConfigString += @"
`$Config += @{
    $("App = '$($App | ConvertTo-Json)' | ConvertFrom-Json")
}`r`n
"@
    }

    if ($DetectConfig) {
        $ConfigString += @"
`$Config += @{
    Script = @{
        $("Detect = '$($DetectConfig | ConvertTo-Json)' | ConvertFrom-Json")
    }
}`r`n
"@
    }

    $ConfigString += "`$PackageOption = '$PackageOption'`r`n`r`n"

    # Inject into template
    $DetectScript = $DetectScript.Replace("# Inject Config.json data here", $ConfigString)

    # Write generated detection script
    $OutputFileName = "Detect-Intune-$AppName-$PackageOption.ps1"
    $OutputFilePath = Join-Path -Path $OutputDirectory -ChildPath $OutputFileName
    Set-Content -Path $OutputFilePath -Value $DetectScript
    $FullPath = (Get-Item -Path $OutputFilePath).FullName

    Write-Log "Generated detection script: $OutputFileName"

    # Create the detection rule
    $DetectionRuleArgs = @{
        ScriptFile            = $FullPath
        EnforceSignatureCheck = $false
        RunAs32Bit            = $false
    }
    $DetectionRule = New-IntuneWin32AppDetectionRuleScript @DetectionRuleArgs

    return $DetectionRule
}
