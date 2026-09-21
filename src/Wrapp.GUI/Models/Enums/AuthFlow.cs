namespace Wrapp.Models;

/// <summary>
/// <para>
/// MSAL authentication flow selected per-tenant for acquiring tokens against
/// Microsoft Graph. Governs which MSAL builder is constructed in
/// <c>MsalAuthService</c>.
/// </para>
/// <para>
/// Serialises as the Pascal-case member name so PowerShell-side comparisons
/// (and existing Config.json / settings.json on-disk data) keep working
/// unchanged. Case-insensitive on read via the global
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>.
/// </para>
/// </summary>
public enum AuthFlow
{
    /// <summary>Interactive browser-based sign-in (delegated user flow). Default.</summary>
    Interactive,
    /// <summary>Device code flow -- displays a code for sign-in on another device.</summary>
    DeviceCode,
    /// <summary>Confidential-client flow using a client secret (app-only).</summary>
    ClientSecret,
    /// <summary>Confidential-client flow using a client certificate (app-only).</summary>
    ClientCert,
}
