using Wrapp.Models;
using Wrapp.Services.Targets;

namespace Wrapp.Tests;

/// <summary>
/// Unit tests for the publish-target framework (capabilities + registry).
/// The concrete targets are constructed with a null inventory service because
/// these tests exercise only capability/dispatch-selection metadata, never the
/// wrapped <c>AppInventoryService</c> calls (which need a live PowerShell host).
/// </summary>
public class PublishTargetTests
{
    private static readonly IntunePublishTarget Intune = new(null!);
    private static readonly SccmPublishTarget Sccm = new(null!);

    // ── Kind / display ──────────────────────────────────────────────

    [Fact]
    public void Targets_ExposeExpectedKindAndDisplayName()
    {
        Assert.Equal(AppPlatform.Intune, Intune.Kind);
        Assert.Equal("Intune", Intune.DisplayName);
        Assert.Equal(AppPlatform.SCCM, Sccm.Kind);
        Assert.Equal("SCCM", Sccm.DisplayName);
    }

    // ── Capabilities ────────────────────────────────────────────────

    [Theory]
    [InlineData(TargetCapabilities.ContentDownload)]
    [InlineData(TargetCapabilities.GroupResolution)]
    [InlineData(TargetCapabilities.ReturnCodes)]
    [InlineData(TargetCapabilities.ScopeTags)]
    [InlineData(TargetCapabilities.RequirementRules)]
    [InlineData(TargetCapabilities.PerPackageDetectionRules)]
    [InlineData(TargetCapabilities.Categories)]
    [InlineData(TargetCapabilities.Assignments)]
    [InlineData(TargetCapabilities.TokenAuth)]
    public void Intune_Supports_IntuneCapabilities(TargetCapabilities cap)
        => Assert.True(Intune.Supports(cap));

    [Theory]
    [InlineData(TargetCapabilities.RepairCommand)]
    [InlineData(TargetCapabilities.InstallBehaviors)]
    [InlineData(TargetCapabilities.Deployments)]
    public void Intune_DoesNotSupport_SccmCapabilities(TargetCapabilities cap)
        => Assert.False(Intune.Supports(cap));

    [Theory]
    [InlineData(TargetCapabilities.RepairCommand)]
    [InlineData(TargetCapabilities.InstallBehaviors)]
    [InlineData(TargetCapabilities.Deployments)]
    public void Sccm_Supports_SccmCapabilities(TargetCapabilities cap)
        => Assert.True(Sccm.Supports(cap));

    [Theory]
    [InlineData(TargetCapabilities.ContentDownload)]
    [InlineData(TargetCapabilities.TokenAuth)]
    [InlineData(TargetCapabilities.Assignments)]
    [InlineData(TargetCapabilities.ReturnCodes)]
    public void Sccm_DoesNotSupport_IntuneCapabilities(TargetCapabilities cap)
        => Assert.False(Sccm.Supports(cap));

    [Fact]
    public void Supports_RequiresAllFlagsInCombination()
    {
        // Intune has Assignments + ScopeTags but not Deployments.
        Assert.True(Intune.Supports(TargetCapabilities.Assignments | TargetCapabilities.ScopeTags));
        Assert.False(Intune.Supports(TargetCapabilities.Assignments | TargetCapabilities.Deployments));
    }

    // ── Registry ────────────────────────────────────────────────────

    private static PublishTargetRegistry BuildRegistry()
        => new(new IPublishTarget[] { Intune, Sccm });

    [Fact]
    public void Registry_Get_ResolvesByKind()
    {
        var reg = BuildRegistry();
        Assert.Same(Intune, reg.Get(AppPlatform.Intune));
        Assert.Same(Sccm, reg.Get(AppPlatform.SCCM));
    }

    [Fact]
    public void Registry_All_ReturnsEveryTarget()
    {
        var reg = BuildRegistry();
        Assert.Equal(2, reg.All.Count);
        Assert.Contains(Intune, reg.All);
        Assert.Contains(Sccm, reg.All);
    }

    [Fact]
    public void Registry_TryGet_ReflectsRegistration()
    {
        var reg = BuildRegistry();
        Assert.True(reg.TryGet(AppPlatform.Intune, out var t));
        Assert.Same(Intune, t);
    }

    [Fact]
    public void Registry_Get_UnregisteredKind_Throws()
    {
        // A registry with only Intune registered.
        var reg = new PublishTargetRegistry(new IPublishTarget[] { Intune });
        Assert.Throws<InvalidOperationException>(() => reg.Get(AppPlatform.SCCM));
    }
}
