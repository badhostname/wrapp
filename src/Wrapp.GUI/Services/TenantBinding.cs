namespace Wrapp.Services;

/// <summary>
/// How a sign-in relates to the tenant it was initialised with.
/// </summary>
public enum TenantBinding
{
    /// <summary>
    /// The tenant supplies the client settings and which cached account to
    /// prefer; the sign-in itself is unscoped and the service adopts the
    /// tenant the token comes back from. The account button: sign in with
    /// any account, discover its tenant.
    /// </summary>
    Discover,

    /// <summary>
    /// The request is made for exactly this tenant and a token for any
    /// other tenant is refused. A packaging run, a tenant card's sign-in,
    /// an inventory query: the tenant is the target, not a suggestion.
    /// </summary>
    Pinned,
}
