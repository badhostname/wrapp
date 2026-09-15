using System.ComponentModel;
using Wrapp.ViewModels;

namespace Wrapp.Tests.ViewModels;

/// <summary>
/// Covers <see cref="PropertyRelay"/>: the source-to-target wiring helper
/// that replaces MainViewModel's hand-rolled PropertyChanged lambdas.
/// </summary>
public class PropertyRelayTests
{
    // Minimal INotifyPropertyChanged source for testing.
    private sealed class SourceStub : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string? name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    [Fact]
    public void Wire_MatchingSource_InvokesTargetRaise()
    {
        var src = new SourceStub();
        var raised = new List<string>();

        PropertyRelay.Wire(src, raised.Add,
            PropertyRelay.When("SourceProp", "TargetProp"));

        src.Raise("SourceProp");

        Assert.Single(raised);
        Assert.Equal("TargetProp", raised[0]);
    }

    [Fact]
    public void Wire_NonMatchingSource_IgnoresEvent()
    {
        var src = new SourceStub();
        var raised = new List<string>();

        PropertyRelay.Wire(src, raised.Add,
            PropertyRelay.When("SourceProp", "TargetProp"));

        src.Raise("UnrelatedProp");

        Assert.Empty(raised);
    }

    [Fact]
    public void Wire_OneToMany_FansOutAllTargets()
    {
        // Mirrors MainViewModel's IsDirty → 5 bubbled properties pattern.
        var src = new SourceStub();
        var raised = new List<string>();

        PropertyRelay.Wire(src, raised.Add,
            PropertyRelay.When("IsDirty", "A", "B", "C"));

        src.Raise("IsDirty");

        Assert.Equal(new[] { "A", "B", "C" }, raised);
    }

    [Fact]
    public void Wire_SharedTargets_BothSourcesFireThem()
    {
        // IsDirty + HasConfig both drive the same set in MainViewModel.
        var src = new SourceStub();
        var raised = new List<string>();
        var targets = new[] { "X", "Y" };

        PropertyRelay.Wire(src, raised.Add,
            PropertyRelay.When("IsDirty",   targets),
            PropertyRelay.When("HasConfig", targets));

        src.Raise("IsDirty");
        src.Raise("HasConfig");

        Assert.Equal(new[] { "X", "Y", "X", "Y" }, raised);
    }

    [Fact]
    public void Wire_MultipleRulesSameEvent_OnlyMatchingFires()
    {
        var src = new SourceStub();
        var raised = new List<string>();

        PropertyRelay.Wire(src, raised.Add,
            PropertyRelay.When("A", "outA"),
            PropertyRelay.When("B", "outB"));

        src.Raise("A");

        Assert.Equal(new[] { "outA" }, raised);
    }

    [Fact]
    public void Wire_NullSource_Throws()
    {
        var raised = new List<string>();
        Assert.Throws<ArgumentNullException>(() =>
            PropertyRelay.Wire(null!, raised.Add, PropertyRelay.When("S", "T")));
    }

    [Fact]
    public void Wire_EmptyRelays_Noop()
    {
        var src = new SourceStub();
        var raised = new List<string>();

        PropertyRelay.Wire(src, raised.Add, Array.Empty<PropertyRelay.Relay>());
        src.Raise("Anything");

        Assert.Empty(raised);
    }

    [Fact]
    public void Wire_NullPropertyName_Ignored()
    {
        // WPF occasionally raises PropertyChanged with PropertyName=null to
        // signal "all properties changed"; our relay should not crash.
        var src = new SourceStub();
        var raised = new List<string>();

        PropertyRelay.Wire(src, raised.Add,
            PropertyRelay.When("S", "T"));

        src.Raise(null);
        Assert.Empty(raised);
    }
}
