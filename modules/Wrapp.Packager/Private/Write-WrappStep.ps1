function Write-WrappStep {
    <#
    .SYNOPSIS
        Emits a structured packaging step event on the output stream for the
        Wrapp .NET run pipeline to consume (Workstream B'). Complements the
        human-readable Write-Log lines: the .NET side maps these typed events to
        per-package progress instead of regex-scraping the log text, so a change
        in log wording can't silently break the progress UI.

    .DESCRIPTION
        Outputs a tagged [pscustomobject] (_Type = 'WrappStep') exactly the way
        the encryption-key emission does, so it flows through the same output
        collection the caller (.NET PackageAsync) already taps via its onOutput
        handler. Objects without a .Success property are ignored by the run
        result parser, so emitting these mid-run is safe.

    .PARAMETER Package
        The package (app) name the step applies to.

    .PARAMETER Step
        The step identifier, e.g. Collision, Wrapping, AppCreation, Dependencies,
        Assignment.

    .PARAMETER Kind
        Start | Success | Fail | Skip | Progress. Progress refines a step that
        is already Running (sub-step status text + percent) without changing
        its state -- used for the fine-grained upload/wrapping sub-steps.

    .PARAMETER TenantId
        The tenant (or site) the step ran against, for per-target progress mapping.

    .PARAMETER ErrorMessage
        Optional failure detail (for Kind = Fail).

    .PARAMETER Detail
        Optional non-error detail (e.g. an assignment applied/failed summary, a
        Graph app id, or a Progress status text) surfaced to the progress UI
        regardless of Kind.

    .PARAMETER Percent
        Sub-step percent (0-100) for Kind = Progress. -1 (default) = not set.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Package,
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][ValidateSet('Start', 'Success', 'Fail', 'Skip', 'Progress')][string]$Kind,
        [string]$TenantId,
        [string]$ErrorMessage,
        [string]$Detail,
        [ValidateRange(-1, 100)][int]$Percent = -1
    )

    [PSCustomObject]@{
        _Type    = 'WrappStep'
        Package  = $Package
        Step     = $Step
        Kind     = $Kind
        TenantId = $TenantId
        Error    = $ErrorMessage
        Detail   = $Detail
        Percent  = $Percent
    }
}
