namespace Wrapp.Services;

/// <summary>
/// MSAL answered a request for one tenant with a token for another - the
/// cached account is not a member of the requested tenant (or the tenant
/// id is not a real tenant), and the request could only be satisfied from
/// the account's home tenant. The token is never handed out under the
/// wrong tenant: the caller reports the tenant as not authenticated.
/// </summary>
public sealed class TenantMismatchException : InvalidOperationException
{
    public string RequestedTenant { get; }
    public string ActualTenant { get; }

    public TenantMismatchException(string requestedTenant, string actualTenant, string? username)
        : base($"The signed-in account{(string.IsNullOrEmpty(username) ? "" : $" ({username})")} is not authenticated to tenant {requestedTenant}; " +
               $"its token is for tenant {actualTenant}. Sign in to the tenant with an account that belongs to it, or check the tenant id.")
    {
        RequestedTenant = requestedTenant;
        ActualTenant = actualTenant;
    }
}
