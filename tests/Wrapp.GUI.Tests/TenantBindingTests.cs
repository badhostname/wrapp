using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Which sign-ins are scoped to a tenant. Discover (the account button)
/// never pins, whatever tenant supplied the client settings - 1.0.8 pinned
/// it and a fresh machine could only cancel the dialog. Pinned (a run, a
/// tenant card, an inventory query) scopes to a real tenant id only.
/// </summary>
public class TenantBindingTests
{
    private const string Tenant = "d35fe7ad-0000-0000-0000-000000000001";

    [Theory]
    [InlineData(TenantBinding.Discover, Tenant, false)]
    [InlineData(TenantBinding.Discover, "", false)]
    [InlineData(TenantBinding.Pinned, Tenant, true)]
    [InlineData(TenantBinding.Pinned, "", false)]
    [InlineData(TenantBinding.Pinned, "organizations", false)]
    [InlineData(TenantBinding.Pinned, null, false)]
    public void Only_A_Pinned_Real_Tenant_Scopes_The_Request(TenantBinding binding, string? tenantId, bool expected)
        => Assert.Equal(expected, MsalAuthService.ShouldPin(binding, tenantId));

    [Fact]
    public void Discover_Is_The_Default_Binding()
    {
        // The account button relies on the default; per-tenant callers opt in.
        var parameter = typeof(MsalAuthService).GetMethod(nameof(MsalAuthService.InitializeForTenantAsync))!
            .GetParameters().Single(p => p.ParameterType == typeof(TenantBinding));
        Assert.True(parameter.HasDefaultValue);
        Assert.Equal(TenantBinding.Discover, parameter.DefaultValue);
    }
}
