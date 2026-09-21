using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Views;

public partial class RunView : UserControl
{
    private bool _scrollWired;

    public RunView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireAutoScroll();
        Loaded += (_, _) => WireAutoScroll();
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is RunViewModel vm)
            SafeFireAndForget.Run(vm.RefreshConnectionStatusAsync, "RunView.RefreshConnections");
    }

    private void WireAutoScroll()
    {
        if (_scrollWired) return;
        if (DataContext is not RunViewModel vm) return;

        vm.LogLines.CollectionChanged += OnLogLinesChanged;
        _scrollWired = true;
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var sv = FindScrollViewer(LogListBox);
                if (sv is null) return;
                sv.ScrollToEnd();     // vertical: follow new lines
                sv.ScrollToLeftEnd(); // horizontal: stay left
            });
        }
    }

    private async void TenantSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not string tenantId) return;
        if (DataContext is not RunViewModel vm) return;
        await vm.SignInTenantAsync(tenantId);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindScrollViewer(child);
            if (result is not null) return result;
        }
        return null;
    }
}
