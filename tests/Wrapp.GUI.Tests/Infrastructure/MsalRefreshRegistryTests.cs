using Microsoft.Identity.Client;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Covers <see cref="MsalRefreshRegistry"/> - the Phase 12 (S-3) backing
/// store for opaque MSAL refresh handles. PowerShell scripts never see the
/// live <see cref="IPublicClientApplication"/>; they only carry the handle
/// produced by <see cref="MsalRefreshRegistry.Register"/>, which the
/// <c>Invoke-WrappTokenRefresh</c> cmdlet resolves via
/// <c>internal</c> <c>TryGet</c>.
/// </summary>
public class MsalRefreshRegistryTests
{
    [Fact]
    public void Register_ProducesUniqueHandlesAndStoresContext()
    {
        var app = PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();
        var scopes = new[] { "https://graph.microsoft.com/.default" };

        var h1 = MsalRefreshRegistry.Register(app, account: null, scopes);
        var h2 = MsalRefreshRegistry.Register(app, account: null, scopes);

        try
        {
            Assert.NotEqual(Guid.Empty, h1);
            Assert.NotEqual(h1, h2);
        }
        finally
        {
            MsalRefreshRegistry.Unregister(h1);
            MsalRefreshRegistry.Unregister(h2);
        }
    }

    [Fact]
    public void Unregister_MakesHandleUnresolvable()
    {
        var app = PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();
        var scopes = new[] { "https://graph.microsoft.com/.default" };
        var handle = MsalRefreshRegistry.Register(app, account: null, scopes);

        MsalRefreshRegistry.Unregister(handle);

        // Idempotent: a second Unregister must not throw.
        MsalRefreshRegistry.Unregister(handle);
    }

    [Fact]
    public void Register_RejectsNullArguments()
    {
        // Null app would defeat the cmdlet's whole reason for existing -- the
        // registry shouldn't admit a stale handle whose IPCA is null. ArgumentNullException
        // surfaces the misuse at the call site instead of waiting for the cmdlet to NRE.
        Assert.Throws<ArgumentNullException>(() =>
            MsalRefreshRegistry.Register(app: null!, account: null, scopes: Array.Empty<string>()));

        var app = PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();
        Assert.Throws<ArgumentNullException>(() =>
            MsalRefreshRegistry.Register(app, account: null, scopes: null!));
    }
}
