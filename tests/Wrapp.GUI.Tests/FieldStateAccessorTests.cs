using System.Windows;
using Wrapp.Helpers;

namespace Wrapp.Tests;

public class FieldStateAccessorTests
{
    // ── Lazy creation + caching ─────────────────────────────────────

    [Fact]
    public void Indexer_FirstAccess_CreatesNewState()
    {
        var a = new FieldStateAccessor();
        var s = a["AppName"];
        Assert.NotNull(s);
    }

    [Fact]
    public void Indexer_SecondAccessSameKey_ReturnsSameInstance()
    {
        // Binding-critical: if we returned a new FieldState each access,
        // WPF would subscribe to the wrong instance and UI would stop
        // updating when FieldStateProvider mutates state.
        var a = new FieldStateAccessor();
        var first  = a["AppName"];
        var second = a["AppName"];
        Assert.Same(first, second);
    }

    [Fact]
    public void Indexer_DifferentKeys_ReturnDifferentInstances()
    {
        var a = new FieldStateAccessor();
        var one = a["AppName"];
        var two = a["Version"];
        Assert.NotSame(one, two);
    }

    [Fact]
    public void Indexer_IsCaseInsensitive()
    {
        // Lets XAML bindings use whichever casing the field declaration
        // uses without silently diverging. The dictionary is constructed
        // with OrdinalIgnoreCase.
        var a = new FieldStateAccessor();
        var lower = a["appname"];
        var upper = a["APPNAME"];
        var mixed = a["AppName"];
        Assert.Same(lower, upper);
        Assert.Same(lower, mixed);
    }

    [Fact]
    public void Tracked_ReflectsExactlyKeysCreatedViaIndexer()
    {
        var a = new FieldStateAccessor();
        _ = a["AppName"];
        _ = a["Publisher"];

        var keys = a.Tracked.Select(kv => kv.Key).ToList();

        Assert.Equal(2, keys.Count);
        Assert.Contains("AppName",   keys);
        Assert.Contains("Publisher", keys);
    }

    [Fact]
    public void Tracked_EmptyBeforeAnyAccess()
    {
        var a = new FieldStateAccessor();
        Assert.Empty(a.Tracked);
    }

    // ── FieldState observability ────────────────────────────────────

    [Fact]
    public void FieldState_DefaultValues_AreEnabledAndVisibleAndNotRequired()
    {
        var s = new FieldState();
        Assert.True(s.IsEnabled);
        Assert.True(s.IsVisible);
        Assert.False(s.IsRequired);
        Assert.Equal(Visibility.Visible, s.Visibility);
        Assert.Null(s.DisabledReason);
        Assert.Null(s.ValidationError);
    }

    [Fact]
    public void FieldState_IsVisibleChange_NotifiesVisibility()
    {
        // Visibility is a computed property -- make sure the Visible
        // -> Collapsed mapping raises change notification for the
        // derived Visibility property, not just IsVisible.
        var s = new FieldState();
        var changes = new List<string>();
        s.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        s.IsVisible = false;

        Assert.Contains(nameof(FieldState.IsVisible),  changes);
        Assert.Contains(nameof(FieldState.Visibility), changes);
        Assert.Equal(Visibility.Collapsed, s.Visibility);
    }

    [Fact]
    public void FieldState_EachObservablePropertyRaisesPropertyChanged()
    {
        var s = new FieldState();
        var seen = new HashSet<string>();
        s.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        s.IsEnabled       = false;
        s.IsRequired      = true;
        s.DisabledReason  = "because";
        s.ValidationError = "bad";
        s.IsVisible       = false;

        Assert.Contains(nameof(FieldState.IsEnabled),       seen);
        Assert.Contains(nameof(FieldState.IsRequired),      seen);
        Assert.Contains(nameof(FieldState.DisabledReason),  seen);
        Assert.Contains(nameof(FieldState.ValidationError), seen);
        Assert.Contains(nameof(FieldState.IsVisible),       seen);
    }

    [Fact]
    public void Indexer_DoesNotLeakStateAcrossAccessors()
    {
        var a1 = new FieldStateAccessor();
        var a2 = new FieldStateAccessor();

        a1["Shared"].IsEnabled = false;

        Assert.False(a1["Shared"].IsEnabled);
        Assert.True(a2["Shared"].IsEnabled);   // independent instance
    }
}
