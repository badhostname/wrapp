using System.Management.Automation;

namespace Wrapp.Models;

/// <summary>
/// A structured packaging step event emitted by the Wrapp.Packager module (via
/// <c>Write-WrappStep</c>) on the PowerShell output stream and consumed by the
/// .NET run pipeline. Incrementally replaces the regex log-scraping in
/// <c>PhaseDetector</c>: the module reports each step's Start/Success/Fail as a
/// typed event rather than prose the .NET side pattern-matches, so a change in
/// the module's log wording can't silently break the progress UI.
/// </summary>
public sealed class StepEvent
{
    /// <summary>The package (app) name the step applies to.</summary>
    public string Package { get; init; } = "";

    /// <summary>Step id: Collision, Wrapping, AppCreation, Dependencies, Assignment.</summary>
    public string Step { get; init; } = "";

    /// <summary>Start | Success | Fail | Skip | Progress.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The tenant / site the step ran against (for per-target mapping).</summary>
    public string? TenantId { get; init; }

    /// <summary>Failure detail when <see cref="Kind"/> is Fail.</summary>
    public string? Error { get; init; }

    /// <summary>Non-error detail (e.g. assignment "3 applied, 0 failed" or a Graph app id).</summary>
    public string? Detail { get; init; }

    /// <summary>Sub-step percent (0-100) for <see cref="Kind"/> Progress; null when not set.</summary>
    public int? Percent { get; init; }

    /// <summary>
    /// Parses a <c>Write-WrappStep</c> output object, or returns null if the
    /// object is not a WrappStep event (e.g. it's the EncryptionKeys object or
    /// the run result).
    /// </summary>
    public static StepEvent? FromPSObject(PSObject obj)
    {
        if (obj.Properties["_Type"]?.Value?.ToString() != "WrappStep") return null;
        return new StepEvent
        {
            Package  = obj.Properties["Package"]?.Value?.ToString() ?? "",
            Step     = obj.Properties["Step"]?.Value?.ToString() ?? "",
            Kind     = obj.Properties["Kind"]?.Value?.ToString() ?? "",
            TenantId = obj.Properties["TenantId"]?.Value?.ToString(),
            Error    = obj.Properties["Error"]?.Value?.ToString(),
            Detail   = obj.Properties["Detail"]?.Value?.ToString(),
            // Module emits -1 for "not set"; normalize to null.
            Percent  = int.TryParse(obj.Properties["Percent"]?.Value?.ToString(), out var pct) && pct >= 0
                       ? pct : null,
        };
    }
}
