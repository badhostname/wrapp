namespace Wrapp.Models;

/// <summary>
/// Holds the token data acquired by MSAL.NET, used by both the C# UI layer
/// and the PowerShell token bridge for injection into IntuneWin32App globals.
/// </summary>
public record MsalTokenResult(
    string AccessToken,
    DateTime ExpiresOnUtc,
    string[] Scopes,
    string TenantId,
    string ClientId,
    string? UserPrincipalName,  // null for app-only flows (ClientSecret, ClientCert)
    string? IdToken = null,
    string? CorrelationId = null
);
