function New-ValidationIssue {
    <#
    .SYNOPSIS
        Creates a structured validation issue object.

    .DESCRIPTION
        Factory function for validation result objects consumed by Test-WrappConfig,
        Test-WrappIntunePreflight, Test-WrappSccmPreflight, and Invoke-FieldSchema.

        The resulting object is designed to support WPF data binding:
          - FieldPath      -> highlight the offending config key in a tree view
          - Severity       -> drive icon/color in a DataGrid
          - Code           -> programmatic filtering and sorting
          - Message        -> main text column
          - AttemptedValue -> show what was provided
          - AllowedValues  -> populate a suggestion dropdown
          - Guidance       -> detail panel shown when the row is selected

        All Test-* functions include an Issues array alongside the existing
        Errors and Warnings string arrays for backward compatibility with
        existing orchestrator callers.

    .PARAMETER FieldPath
        Dot-path to the offending field in Config.json.
        Examples: 'Packages[MyApp].InstallExperience'
                  'IntuneTenant[Prod].AuthFlow'
                  'SCCMSite[CB1].Deployments[0].DeployPurpose'

    .PARAMETER Severity
        Severity level. 'Error' (fatal, prevents execution) or 'Warning' (non-fatal, advisory).
        Default: 'Error'

    .PARAMETER Code
        Machine-readable identifier. Standard codes:
            MISSING_REQUIRED    - required field absent or empty
            INVALID_ENUM_VALUE  - value not in allowed set
            INVALID_URL         - URL pattern failed
            OUT_OF_RANGE        - numeric value outside min/max
            WRONG_TYPE          - value is not the expected .NET type
            UNKNOWN_FIELD       - unrecognized parameter key
            SELF_REFERENCE      - app references itself in dep/supersedence
            CIRCULAR_DEP        - circular dependency detected
            MUTUAL_SUPERSEDENCE - A supersedes B and B supersedes A
            GUID_FORMAT         - GUID format invalid
            CERT_FORMAT         - certificate thumbprint format invalid
            CERT_NOT_FOUND      - certificate not in local stores
            ENTITY_NOT_FOUND    - referenced entity not in Graph or SCCM
            COUNT_EXCEEDED      - array length exceeds maximum
            REQUIRES_FIELD      - field X requires field Y to also be set
            INVALID_COMBINATION - combination of values is not allowed
            SCHEDULE_ORDER      - deadline is not after available time
            RANGE_EXCEEDED      - one value exceeds another

    .PARAMETER Message
        Human-readable description of the failure. Should include the field name,
        the attempted value, and what was expected.

    .PARAMETER AttemptedValue
        The value that was provided and failed validation. Used for display.

    .PARAMETER AllowedValues
        Array of values that would be accepted. Used for dropdown suggestions in the GUI.

    .PARAMETER Guidance
        Educational context explaining why the constraint exists and how to fix it.
        This is the text displayed in the WPF detail panel when the row is selected.

    .OUTPUTS
        [PSCustomObject] with FieldPath, Severity, Code, Message, AttemptedValue,
        AllowedValues, Guidance properties.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FieldPath,

        [ValidateSet('Error', 'Warning')]
        [string]$Severity = 'Error',

        [Parameter(Mandatory = $true)]
        [string]$Code,

        [Parameter(Mandatory = $true)]
        [string]$Message,

        $AttemptedValue = $null,

        [string[]]$AllowedValues = @(),

        [string]$Guidance = ''
    )

    [PSCustomObject]@{
        FieldPath      = $FieldPath
        Severity       = $Severity
        Code           = $Code
        Message        = $Message
        AttemptedValue = $AttemptedValue
        AllowedValues  = $AllowedValues
        Guidance       = $Guidance
    }
}
