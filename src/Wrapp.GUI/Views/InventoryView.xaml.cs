using System.Windows;
using System.Windows.Controls;
using Wrapp.Controls;
using Wrapp.Services;

namespace Wrapp.Views;

public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
    }

    private async void SearchHelp_Click(object sender, RoutedEventArgs e)
    {
        var content = Application.Current.TryFindResource("Help.Inventory.Search") as string;
        if (string.IsNullOrEmpty(content)) return;

        var panel = SectionHeader.BuildFormattedPanel(content, this);
        await FluentDialog.ShowScrollableContentAsync("Search Help", panel, "Close");
    }
}
