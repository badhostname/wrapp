using System.Windows;
using System.Windows.Controls;
using Wrapp.Models;

namespace Wrapp.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // The VM's dirty-check serializer runs only while
        // this view is on screen (its inputs can't change otherwise).
        IsVisibleChanged += (_, e) =>
        {
            if (DataContext is ViewModels.SettingsViewModel vm)
                vm.SetChangeTrackingActive((bool)e.NewValue);
        };
    }

    /// <summary>
    /// PasswordBox doesn't support TwoWay binding (by WPF design, to discourage
    /// holding SecurePassword strings in the visual tree). Write the typed
    /// value back to the row's <see cref="IntuneTenantEntry.ClientSecret"/>
    /// on change so the save path picks it up. An empty PasswordBox leaves
    /// ClientSecret unset (zero-length) - the save path then preserves the
    /// existing <see cref="IntuneTenantEntry.ClientSecretCipher"/> unchanged.
    /// <para>
    /// <see cref="PasswordBox.SecurePassword"/> hands the
    /// plaintext to the entry as a <see cref="System.Security.SecureString"/>,
    /// so the typed value never lives in a managed <see cref="string"/>. The
    /// old entry value is disposed before assignment to release its
    /// encrypted buffer immediately.
    /// </para>
    /// </summary>
    private void ClientSecretEditor_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && pb.DataContext is IntuneTenantEntry tenant)
        {
            tenant.ClientSecret?.Dispose();
            tenant.ClientSecret = pb.SecurePassword;
        }
    }

    /// <summary>
    /// Same PasswordBox write-back pattern for sensitive
    /// placeholder values. The typed plaintext parks on the row's
    /// <see cref="ViewModels.PlaceholderRowVm.PendingSecretValue"/> until Save
    /// DPAPI-encrypts it into the sidecar; the stored value is never read back
    /// into the UI. (Plain string rather than SecureString because the
    /// placeholder pipeline materializes values as strings by design - they
    /// are substituted into scripts and fields.)
    /// </summary>
    private void PlaceholderSecretEditor_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && pb.DataContext is ViewModels.PlaceholderRowVm row)
            row.PendingSecretValue = pb.Password;
    }
}
