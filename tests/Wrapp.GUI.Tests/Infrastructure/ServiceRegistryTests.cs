using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Unit tests for <see cref="ServiceRegistry"/> - the type-keyed store behind
/// the composition root. Uses local sample types so no real services spin up.
/// </summary>
public class ServiceRegistryTests
{
    private interface ISample { }
    private sealed class Sample : ISample { public int Id; }
    private sealed class Other { }

    [Fact]
    public void Register_ThenGet_ReturnsSameInstance()
    {
        var reg = new ServiceRegistry();
        var s = new Sample { Id = 7 };

        var returned = reg.Register(s);

        Assert.Same(s, returned);          // Register returns the instance for inline use
        Assert.Same(s, reg.Get<Sample>());
    }

    [Fact]
    public void Register_KeysByCompileTimeType_NotRuntimeType()
    {
        var reg = new ServiceRegistry();
        var s = new Sample();

        reg.Register<ISample>(s);          // registered under the interface

        Assert.Same(s, reg.Get<ISample>());
        Assert.False(reg.Contains<Sample>()); // NOT under the concrete type
    }

    [Fact]
    public void Register_ReplacesExisting()
    {
        var reg = new ServiceRegistry();
        var first = new Sample { Id = 1 };
        var second = new Sample { Id = 2 };

        reg.Register(first);
        reg.Register(second);

        Assert.Same(second, reg.Get<Sample>());
    }

    [Fact]
    public void Get_Unregistered_Throws()
    {
        var reg = new ServiceRegistry();
        var ex = Assert.Throws<InvalidOperationException>(() => reg.Get<Sample>());
        Assert.Contains("Sample", ex.Message);
    }

    [Fact]
    public void TryGet_Registered_ReturnsTrueAndInstance()
    {
        var reg = new ServiceRegistry();
        var s = new Sample();
        reg.Register(s);

        Assert.True(reg.TryGet<Sample>(out var got));
        Assert.Same(s, got);
    }

    [Fact]
    public void TryGet_Unregistered_ReturnsFalseAndNull()
    {
        var reg = new ServiceRegistry();

        Assert.False(reg.TryGet<Other>(out var got));
        Assert.Null(got);
    }

    [Fact]
    public void Contains_ReflectsRegistration()
    {
        var reg = new ServiceRegistry();
        Assert.False(reg.Contains<Sample>());
        reg.Register(new Sample());
        Assert.True(reg.Contains<Sample>());
    }
}
