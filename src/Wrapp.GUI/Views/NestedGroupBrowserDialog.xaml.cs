using System.Windows;
using System.Windows.Controls;
using Wrapp.Models;

namespace Wrapp.Views;

public partial class NestedGroupBrowserDialog : UserControl
{
    public NestedGroupBrowserDialog(NestedGroupData data, string searchQuery = "")
    {
        InitializeComponent();

        HeaderSection.Title = data.RootDisplayName;

        var nestedCount = data.AllNestedGroupNames.Count;
        var stats = $"{nestedCount} nested group(s), {data.MaxDepth} level(s) deep";
        if (data.HasCircularReference)
            stats += " (circular reference detected)";
        StatsText.Text = stats;

        if (data.TreeRoot is not null && data.TreeRoot.Children.Count > 0)
        {
            // Clear any stale highlights from previous searches
            ClearSearchMatches(data.TreeRoot);

            if (!string.IsNullOrWhiteSpace(searchQuery))
                MarkSearchMatches(data.TreeRoot, searchQuery.Trim().ToLowerInvariant());

            GroupTree.ItemsSource = data.TreeRoot.Children;
        }
        else
        {
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private static void ClearSearchMatches(NestedGroupNode node)
    {
        node.IsSearchMatch = false;
        foreach (var child in node.Children)
            ClearSearchMatches(child);
    }

    private static void MarkSearchMatches(NestedGroupNode node, string query)
    {
        node.IsSearchMatch = node.DisplayName.Contains(query, System.StringComparison.OrdinalIgnoreCase);
        foreach (var child in node.Children)
            MarkSearchMatches(child, query);
    }

    private void GroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NestedGroupNode node)
        {
            DetailPanel.Visibility = Visibility.Visible;
            DetailPlaceholder.Visibility = Visibility.Collapsed;

            DetailHeader.Text = node.DisplayName;
            DetailName.Text = node.DisplayName;
            DetailId.Text = node.GroupId;
            DetailType.Text = !string.IsNullOrEmpty(node.GroupType) ? node.GroupType : "-";
            DetailDescription.Text = !string.IsNullOrEmpty(node.Description) ? node.Description : "-";
            DetailMail.Text = !string.IsNullOrEmpty(node.Mail) ? node.Mail : "-";
            DetailVisibility.Text = !string.IsNullOrEmpty(node.Visibility) ? node.Visibility : "-";
            DetailSecurity.Text = node.SecurityEnabled ? "Yes" : "No";
            DetailCreated.Text = !string.IsNullOrEmpty(node.CreatedDateTime) ? node.CreatedDateTime : "-";
            DetailChildCount.Text = node.Children.Count.ToString();
            DetailMemberCount.Text = node.MemberCount >= 0
                ? node.MemberCount.ToString()
                : "Not available";
        }
        else
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailPlaceholder.Visibility = Visibility.Visible;
            DetailHeader.Text = "(select a group)";
        }
    }
}
