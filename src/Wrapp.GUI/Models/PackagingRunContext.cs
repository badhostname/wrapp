namespace Wrapp.Models;

/// <summary>
/// Frozen snapshot of a packaging run's deployment plan, attached to the
/// BackgroundJob so the pop-up's expanded view can render the same tree as the
/// pre-run confirmation dialog. Captured at run-kick-off so it reflects exactly
/// what the user approved, not live state that may have drifted during the run.
/// </summary>
public sealed class PackagingRunContext
{
    public string Mode          { get; init; } = "";
    public string Target        { get; init; } = "";
    public string BundleRootDir { get; init; } = "";

    public List<PackagingRunIntunePackage> IntunePackages { get; init; } = new();
    public List<PackagingRunSccmPackage>   SccmPackages   { get; init; } = new();
}

public sealed class PackagingRunIntunePackage
{
    public string AppName    { get; init; } = "";
    public UpdateMode UpdateMode { get; init; } = UpdateMode.Create;
    public string TenantId   { get; init; } = "";

    /// <summary>Resolved tenant display name, or null when no connected tenant matches.</summary>
    public string? TenantDisplayName { get; init; }

    public List<PackagingRunAssignment> Assignments { get; init; } = new();

    /// <summary>
    /// Stamped at run completion from the matching <c>PackageProgress</c>. Null
    /// while the run is still in-flight. Used by the Background Jobs expanded
    /// card to colour-highlight the package row after a run completes.
    /// </summary>
    public PackageOutcome? Outcome { get; set; }

    /// <summary>Short reason a package failed, surfaced in the expanded card.</summary>
    public string? FailureReason { get; set; }
}

public sealed class PackagingRunSccmPackage
{
    public string AppName  { get; init; } = "";
    public string SiteCode { get; init; } = "";

    /// <summary>Resolved site display name, or null when no connected site matches.</summary>
    public string? SiteDisplayName { get; init; }

    public List<PackagingRunDeployment> Deployments { get; init; } = new();

    /// <summary>See <see cref="PackagingRunIntunePackage.Outcome"/>.</summary>
    public PackageOutcome? Outcome { get; set; }

    /// <summary>Short reason a package failed, surfaced in the expanded card.</summary>
    public string? FailureReason { get; set; }
}

public sealed class PackagingRunAssignment
{
    public string Intent       { get; init; } = "";
    public string Type         { get; init; } = "";
    public string GroupMode    { get; init; } = "";
    public string GroupID      { get; init; } = "";
    public string Label        { get; init; } = "";
    public string DisplayName  { get; init; } = "";
    public string Notification { get; init; } = "";
}

public sealed class PackagingRunDeployment
{
    public string DeployPurpose { get; init; } = "";
    public string DeployAction  { get; init; } = "";
    public string Collection    { get; init; } = "";
    public string Label         { get; init; } = "";
    public string DisplayName   { get; init; } = "";
}

/// <summary>
/// Summary stamped on Complete/Fail for a packaging run. Rendered as a small
/// detail line beneath the card title when the user inspects a completed run.
/// </summary>
public sealed class PackagingRunSummary
{
    public int PackagesAttempted { get; init; }
    public int PackagesSucceeded { get; init; }
    public int PackagesFailed    { get; init; }
    public int TenantsTargeted   { get; init; }
    public int AssignmentsApplied { get; init; }

    public string OneLine
        => $"{PackagesSucceeded}/{PackagesAttempted} package(s) across {TenantsTargeted} tenant(s)"
         + (PackagesFailed   > 0 ? $", {PackagesFailed} failed"    : "")
         + (AssignmentsApplied > 0 ? $"; {AssignmentsApplied} assignment(s) applied" : "");
}
