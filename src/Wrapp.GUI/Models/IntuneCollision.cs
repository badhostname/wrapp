namespace Wrapp.Models;

/// <summary>
/// An existing Intune app whose display name matches a package about to be
/// created. Produced by the module's <c>Test-Win32AppCollisions</c> and
/// surfaced in the deployment-plan dialog so the operator decides BEFORE the
/// run wraps anything.
/// </summary>
/// <param name="PackageName">The bundle package that collides.</param>
/// <param name="ExistingAppName">Display name of the app already in Intune.</param>
/// <param name="Version">Version of the existing app, when reported.</param>
/// <param name="Publisher">Publisher of the existing app, when reported.</param>
/// <param name="Id">
/// Intune app object ID - what a package needs in <c>ExistingAppID</c> to
/// switch from Create to Update mode.
/// </param>
public sealed record IntuneCollision(
    string PackageName,
    string ExistingAppName,
    string Version,
    string Publisher,
    string Id);
