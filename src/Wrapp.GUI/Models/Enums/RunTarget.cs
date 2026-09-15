namespace Wrapp.Models;

/// <summary>
/// Which destination(s) a packaging run publishes to. Distinct from
/// <see cref="AppPlatform"/> because a run can target <see cref="Both"/> at
/// once (fan a single definition to Intune and SCCM), whereas an inventoried
/// app belongs to exactly one platform. Replaces the previous magic-string
/// discriminator (<c>"Intune" | "SCCM" | "Both"</c>) on <c>RunViewModel</c>.
/// </summary>
public enum RunTarget
{
    /// <summary>Publish to Microsoft Intune only.</summary>
    Intune,
    /// <summary>Publish to Microsoft Configuration Manager (SCCM) only.</summary>
    SCCM,
    /// <summary>Publish to both Intune and SCCM in one run.</summary>
    Both,
}
