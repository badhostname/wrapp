using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// Regression tests for the package-state radio bug: two grouped RadioButtons
/// TwoWay-bound to IsEnabled let the radio group's mutual-uncheck WRITE
/// through a stale binding chain during a selection change — silently
/// re-enabling the package being navigated away from. This harness reproduces
/// the hazard headless (a chained-path binding caches its intermediate
/// object, so the group-uncheck write lands on the previous package), which
/// also disproved a first fix attempt that only moved the binding anchor.
/// <para>The shipped fix makes writes GESTURE-ONLY: UpdateSourceTrigger=
/// Explicit with UpdateSource called from Checked (mirrored here from the
/// views' PackageStateRadio_Checked handler). Unchecked never writes — that
/// was the bug's vector — and a Checked raised by a transition target-update
/// writes back the value it just read, a no-op.</para>
/// </summary>
public partial class PackageStateRadioRegressionTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private sealed partial class FakeVm : ObservableObject
    {
        [ObservableProperty] private IntunePackageEntry? _selectedPackage;
    }

    private sealed class InvertBool : IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
            => value is bool b && !b;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c)
            => value is bool b && !b;
    }

    /// <summary>
    /// Two grouped radios in the binding shape that DEMONSTRABLY reproduces
    /// the stale-write hazard headless (chained path, cached intermediate),
    /// armed with the shipped defusal: Explicit update trigger + the views'
    /// Checked→UpdateSource handler. If the handler or trigger regresses,
    /// the navigation tests fail exactly like the original bug.
    /// </summary>
    private static (FakeVm Vm, RadioButton Enabled, RadioButton Disabled) BuildCard()
    {
        var vm = new FakeVm();
        var enabled  = new RadioButton { GroupName = "PkgState" };
        var disabled = new RadioButton { GroupName = "PkgState" };
        var panel = new StackPanel();
        panel.Children.Add(enabled);
        panel.Children.Add(disabled);

        Binding Make(bool invert) => new("SelectedPackage.IsEnabled")
        {
            Source = vm,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.Explicit,
            Converter = invert ? new InvertBool() : null,
        };
        enabled.SetBinding(ToggleButton.IsCheckedProperty, Make(invert: false));
        disabled.SetBinding(ToggleButton.IsCheckedProperty, Make(invert: true));

        // Mirror of IntuneView/SCCMView.PackageStateRadio_Checked.
        void OnChecked(object s, RoutedEventArgs e)
            => ((ToggleButton)s).GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        enabled.Checked += OnChecked;
        disabled.Checked += OnChecked;

        return (vm, enabled, disabled);
    }

    [Fact]
    public void NavigatingAwayFromDisabled_DoesNotReEnableIt()
        => RunSta(() =>
        {
            var (vm, enabledRadio, disabledRadio) = BuildCard();
            var a = new IntunePackageEntry { AppName = "7-Zip" };
            var b = new IntunePackageEntry { AppName = "7-Zip" };

            vm.SelectedPackage = b;
            disabledRadio.IsChecked = true;    // operator disables B on its pane
            Assert.False(b.IsEnabled);

            vm.SelectedPackage = a;            // ...then clicks A

            Assert.False(b.IsEnabled);         // B must stay disabled
            Assert.True(a.IsEnabled);
            Assert.True(enabledRadio.IsChecked);
        });

    [Fact]
    public void NavigatingToDisabled_DoesNotDisableTheOneLeft()
        => RunSta(() =>
        {
            var (vm, _, disabledRadio) = BuildCard();
            var a = new IntunePackageEntry();
            var b = new IntunePackageEntry { IsEnabled = false };

            vm.SelectedPackage = a;
            vm.SelectedPackage = b;            // reverse direction

            Assert.True(a.IsEnabled);          // A must stay enabled
            Assert.False(b.IsEnabled);
            Assert.True(disabledRadio.IsChecked);
        });

    [Fact]
    public void RadioClicks_ToggleOnlyTheSelectedPackage()
        => RunSta(() =>
        {
            var (vm, enabledRadio, disabledRadio) = BuildCard();
            var selected = new IntunePackageEntry();
            var bystander = new IntunePackageEntry { IsEnabled = false };
            vm.SelectedPackage = selected;
            Assert.True(enabledRadio.IsChecked);

            disabledRadio.IsChecked = true;    // the operator's click
            Assert.False(selected.IsEnabled);
            enabledRadio.IsChecked = true;
            Assert.True(selected.IsEnabled);

            Assert.False(bystander.IsEnabled); // untouched throughout
        });

    [Fact]
    public void ExternalStateChange_ReflectsInRadios()
        => RunSta(() =>
        {
            var (vm, enabledRadio, disabledRadio) = BuildCard();
            var pkg = new IntunePackageEntry();
            vm.SelectedPackage = pkg;

            pkg.IsEnabled = false;             // e.g. changed by a run policy
            Assert.True(disabledRadio.IsChecked);
            Assert.False(enabledRadio.IsChecked);
        });

    [Fact]
    public void RapidSelectionChurn_NeverFlipsAnyPackage()
        => RunSta(() =>
        {
            var (vm, _, _) = BuildCard();
            var enabled1  = new IntunePackageEntry { AppName = "7-Zip" };
            var disabled1 = new IntunePackageEntry { AppName = "7-Zip", IsEnabled = false };
            var enabled2  = new IntunePackageEntry { AppName = "VLC" };
            var disabled2 = new IntunePackageEntry { AppName = "VLC", IsEnabled = false };

            // Bounce across every combination of enabled/disabled targets.
            foreach (var pkg in new[] { enabled1, disabled1, enabled2, disabled2,
                                        disabled1, enabled1, disabled2, enabled2, disabled1 })
                vm.SelectedPackage = pkg;

            Assert.True(enabled1.IsEnabled);
            Assert.True(enabled2.IsEnabled);
            Assert.False(disabled1.IsEnabled);
            Assert.False(disabled2.IsEnabled);
        });
}
