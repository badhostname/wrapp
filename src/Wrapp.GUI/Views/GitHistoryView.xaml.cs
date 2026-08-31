using System.Windows.Controls;
using System.Windows.Input;
using Wrapp.Models;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class GitHistoryView : UserControl
{
    public GitHistoryView()
    {
        InitializeComponent();

        // The 5s git poll (2-3 process spawns per tick) runs only
        // while the History view is on screen; hidden history can't be seen,
        // and becoming visible triggers an immediate catch-up poll.
        IsVisibleChanged += (_, e) =>
        {
            if (DataContext is GitHistoryViewModel vm)
                vm.SetPollingActive((bool)e.NewValue);
        };
    }

    private void CommitItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (item.DataContext is not CommitInfo commit) return;
        if (DataContext is not GitHistoryViewModel vm) return;

        vm.ViewCommitCommand.Execute(commit);
    }
}
