namespace Wrapp.Models;

/// <summary>
/// <para>
/// Controls how a packaging run treats an Intune app that may already exist:
/// create a brand-new app, update its metadata only, or update metadata plus
/// binary content.
/// </para>
/// <para>
/// Serialises as the Pascal-case member name (<c>"Create"</c>, <c>"Update"</c>,
/// <c>"UpdateContent"</c>) so the Wrapp.Packager PowerShell module&#x2019;s
/// <c>-eq 'Create'</c> / <c>-in @('Update','UpdateContent')</c> comparisons
/// keep working unchanged. Deserialisation is case-insensitive via the
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// registered on <c>JsonDefaults.PrettyUnsafe</c>.
/// </para>
/// </summary>
public enum UpdateMode
{
    /// <summary>Create a new Intune app. Collision check aborts if the display name already exists.</summary>
    Create,
    /// <summary>Update metadata of an existing app identified by <c>ExistingAppID</c>. Collision check is skipped.</summary>
    Update,
    /// <summary>Update metadata AND the intunewin binary of an existing app. Collision check is skipped.</summary>
    UpdateContent,
}
