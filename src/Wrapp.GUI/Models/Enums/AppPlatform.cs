namespace Wrapp.Models;

/// <summary>
/// <para>
/// Deployment platform an inventoried application targets. Drives which
/// pane is shown in the inventory view and which set of operations
/// (Intune Graph vs SCCM cmdlet) apply to a given entry.
/// </para>
/// <para>
/// Named <c>AppPlatform</c> (rather than <c>Platform</c>) to avoid colliding
/// with <c>System.Reflection.PortableExecutable.Platform</c> and similar
/// BCL names that appear in WPF+MSAL dependency closures. Serialises as
/// the Pascal-case member name; case-insensitive on read.
/// </para>
/// </summary>
public enum AppPlatform
{
    /// <summary>Microsoft Intune Win32 app.</summary>
    Intune,
    /// <summary>Microsoft Configuration Manager (SCCM) application.</summary>
    SCCM,
}
