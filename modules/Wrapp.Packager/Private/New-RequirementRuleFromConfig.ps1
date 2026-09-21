function New-RequirementRuleFromConfig {
    <#
    .SYNOPSIS
        Builds additional requirement rule objects from config arrays.

    .DESCRIPTION
        Maps config objects to New-IntuneWin32AppRequirementRuleFile,
        New-IntuneWin32AppRequirementRuleRegistry, or
        New-IntuneWin32AppRequirementRuleScript calls.

    .PARAMETER RequirementRules
        Array of requirement rule config objects with a Type field
        (File, Registry, or Script).

    .OUTPUTS
        Array of OrderedDictionary requirement rule objects.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [array]$RequirementRules
    )

    $rules = @()

    foreach ($rr in $RequirementRules) {
        if (-not $rr.Type) {
            Write-Log "Requirement rule missing 'Type' field, skipping" -Type 2
            continue
        }

        try {
            switch ($rr.Type) {
                'File' {
                    $baseParams = @{
                        Path             = $rr.Path
                        FileOrFolder     = $rr.FileOrFolder
                    }
                    if ($null -ne $rr.Check32BitOn64System) {
                        $baseParams['Check32BitOn64System'] = [bool]$rr.Check32BitOn64System
                    }

                    $method = if ($rr.DetectionMethod) { $rr.DetectionMethod } else { 'Existence' }
                    switch ($method) {
                        'Existence' {
                            $baseParams['Existence'] = $true
                            $baseParams['DetectionType'] = if ($rr.DetectionType) { $rr.DetectionType } else { 'exists' }
                            $rules += New-IntuneWin32AppRequirementRuleFile @baseParams
                        }
                        'DateModified' {
                            $baseParams['DateModified'] = $true
                            $baseParams['Operator'] = $rr.Operator
                            $baseParams['DateTimeValue'] = [datetime]::Parse($rr.Value)
                            $rules += New-IntuneWin32AppRequirementRuleFile @baseParams
                        }
                        'DateCreated' {
                            $baseParams['DateCreated'] = $true
                            $baseParams['Operator'] = $rr.Operator
                            $baseParams['DateTimeValue'] = [datetime]::Parse($rr.Value)
                            $rules += New-IntuneWin32AppRequirementRuleFile @baseParams
                        }
                        'Version' {
                            $baseParams['Version'] = $true
                            $baseParams['Operator'] = $rr.Operator
                            $baseParams['VersionValue'] = $rr.Value
                            $rules += New-IntuneWin32AppRequirementRuleFile @baseParams
                        }
                        'Size' {
                            $baseParams['Size'] = $true
                            $baseParams['Operator'] = $rr.Operator
                            $baseParams['SizeInMBValue'] = [string]$rr.Value
                            $rules += New-IntuneWin32AppRequirementRuleFile @baseParams
                        }
                        default {
                            Write-Log "Unknown File requirement method '$method', skipping" -Type 2
                        }
                    }
                }
                'Registry' {
                    $baseParams = @{
                        KeyPath = $rr.KeyPath
                    }
                    if ($rr.ValueName) { $baseParams['ValueName'] = $rr.ValueName }
                    if ($null -ne $rr.Check32BitOn64System) {
                        $baseParams['Check32BitOn64System'] = [bool]$rr.Check32BitOn64System
                    }

                    $method = if ($rr.DetectionMethod) { $rr.DetectionMethod } else { 'Existence' }
                    switch ($method) {
                        'Existence' {
                            $baseParams['Existence'] = $true
                            $baseParams['DetectionType'] = if ($rr.DetectionType) { $rr.DetectionType } else { 'exists' }
                            $rules += New-IntuneWin32AppRequirementRuleRegistry @baseParams
                        }
                        'StringComparison' {
                            $baseParams['StringComparison'] = $true
                            $baseParams['StringComparisonOperator'] = $rr.Operator
                            $baseParams['StringComparisonValue'] = $rr.Value
                            $rules += New-IntuneWin32AppRequirementRuleRegistry @baseParams
                        }
                        'IntegerComparison' {
                            $baseParams['IntegerComparison'] = $true
                            $baseParams['IntegerComparisonOperator'] = $rr.Operator
                            $baseParams['IntegerComparisonValue'] = [string]$rr.Value
                            $rules += New-IntuneWin32AppRequirementRuleRegistry @baseParams
                        }
                        'VersionComparison' {
                            $baseParams['VersionComparison'] = $true
                            $baseParams['VersionComparisonOperator'] = $rr.Operator
                            $baseParams['VersionComparisonValue'] = $rr.Value
                            $rules += New-IntuneWin32AppRequirementRuleRegistry @baseParams
                        }
                        default {
                            Write-Log "Unknown Registry requirement method '$method', skipping" -Type 2
                        }
                    }
                }
                'Script' {
                    if (-not $rr.ScriptFile -or -not (Test-Path -Path $rr.ScriptFile)) {
                        Write-Log "Script requirement rule: ScriptFile '$($rr.ScriptFile)' not found, skipping" -Type 2
                        continue
                    }

                    $baseParams = @{
                        ScriptFile    = $rr.ScriptFile
                        ScriptContext = if ($rr.ScriptContext) { $rr.ScriptContext } else { 'system' }
                    }
                    if ($null -ne $rr.RunAs32BitOn64System) {
                        $baseParams['RunAs32BitOn64System'] = [bool]$rr.RunAs32BitOn64System
                    }
                    if ($null -ne $rr.EnforceSignatureCheck) {
                        $baseParams['EnforceSignatureCheck'] = [bool]$rr.EnforceSignatureCheck
                    }

                    $outputType = if ($rr.OutputDataType) { $rr.OutputDataType } else { 'String' }
                    switch ($outputType) {
                        'String' {
                            $baseParams['StringOutputDataType'] = $true
                            $baseParams['StringComparisonOperator'] = $rr.Operator
                            $baseParams['StringValue'] = $rr.Value
                        }
                        'Integer' {
                            $baseParams['IntegerOutputDataType'] = $true
                            $baseParams['IntegerComparisonOperator'] = $rr.Operator
                            $baseParams['IntegerValue'] = [string]$rr.Value
                        }
                        'Boolean' {
                            $baseParams['BooleanOutputDataType'] = $true
                            $baseParams['BooleanComparisonOperator'] = $rr.Operator
                            $baseParams['BooleanValue'] = [string]$rr.Value
                        }
                        'DateTime' {
                            $baseParams['DateTimeOutputDataType'] = $true
                            $baseParams['DateTimeComparisonOperator'] = $rr.Operator
                            $baseParams['DateTimeValue'] = [datetime]::Parse($rr.Value)
                        }
                        'Float' {
                            $baseParams['FloatOutputDataType'] = $true
                            $baseParams['FloatComparisonOperator'] = $rr.Operator
                            $baseParams['FloatValue'] = [string]$rr.Value
                        }
                        'Version' {
                            $baseParams['VersionOutputDataType'] = $true
                            $baseParams['VersionComparisonOperator'] = $rr.Operator
                            $baseParams['VersionValue'] = $rr.Value
                        }
                        default {
                            Write-Log "Unknown Script requirement OutputDataType '$outputType', skipping" -Type 2
                            continue
                        }
                    }

                    $rules += New-IntuneWin32AppRequirementRuleScript @baseParams
                }
                default {
                    Write-Log "Unknown requirement rule type '$($rr.Type)', skipping" -Type 2
                }
            }
        }
        catch {
            Write-Log "Failed to create requirement rule (Type=$($rr.Type)): $_" -Type 3
        }
    }

    return $rules
}
