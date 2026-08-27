function Test-EnumField {
    <#
    .SYNOPSIS
        Validates that a config field value is one of an allowed enum set,
        and emits a structured INVALID_ENUM_VALUE error if it is not.

    .DESCRIPTION
        Phase 14 (D-3). Test-WrappConfig.ps1 previously carried 26
        copy-paste enum-validation blocks of the shape:

            if ($obj.Field -and $obj.Field -notin $D.ValidXxx) {
                $p = @{
                    FieldPath      = "$baseLabel.Field"
                    Code           = 'INVALID_ENUM_VALUE'
                    Msg            = "Context 'XYZ': Field '$($obj.Field)' invalid. Must be one of: $($D.ValidXxx -join ', ')"
                    AttemptedValue = $obj.Field
                    AllowedValues  = $D.ValidXxx
                    Guidance       = '...'
                }
                Add-Error @p
            }

        This helper collapses each block to a single call. Empty / null
        values pass through (the validator only fires when the field has a
        non-empty value), matching the prior "if (`$obj.Field -and ...)"
        guard. The Add-Error scriptblock is the call site's local closure
        over its nested Add-Error helper -- passed in so the helper does
        not need to reach into the caller's scope.

    .PARAMETER Value
        The actual value to validate. Null or empty values short-circuit
        successfully (matching the prior "-and" guard).

    .PARAMETER ValidValues
        The allowed enum members.

    .PARAMETER FieldPath
        Dotted path used in the resulting ValidationIssue.

    .PARAMETER Label
        Human-readable context for the error message (e.g.
        "SCCM deployment 'depA': DeployAction").

    .PARAMETER Guidance
        Operator-facing remediation text.

    .PARAMETER AddError
        Scriptblock that receives the error parameter hashtable and forwards
        it to the caller's Add-Error helper. Conventional form:
        `-AddError { param($p) Add-Error @p }`.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyString()]
        $Value,

        [Parameter(Mandatory)]
        [string[]]$ValidValues,

        [Parameter(Mandatory)]
        [string]$FieldPath,

        [Parameter(Mandatory)]
        [string]$Label,

        [string]$Guidance = '',

        [Parameter(Mandatory)]
        [scriptblock]$AddError
    )

    if ([string]::IsNullOrEmpty($Value)) { return }
    if ($Value -in $ValidValues) { return }

    $p = @{
        FieldPath      = $FieldPath
        Code           = 'INVALID_ENUM_VALUE'
        Msg            = "${Label}: '$Value' invalid. Must be one of: $($ValidValues -join ', ')"
        AttemptedValue = $Value
        AllowedValues  = $ValidValues
        Guidance       = $Guidance
    }
    & $AddError $p
}
