using System.Collections.ObjectModel;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wrapp.Models;
using Microsoft.Win32;

namespace Wrapp.Views;

public partial class RegistryBrowserDialog : UserControl
{
    private readonly ObservableCollection<RegistryTreeNode> _roots;

    /// <summary>The selected registry path in PowerShell format (HKLM:\... or HKCU:\...).</summary>
    public string? SelectedPath { get; private set; }

    /// <summary>The selected value name, or "Exists" if only a key was selected.</summary>
    public string SelectedProperty { get; private set; } = "Exists";

    /// <summary>True when a valid selection has been made.</summary>
    public bool HasSelection { get; private set; }

    /// <summary>All value names for the selected key ("Exists" + each value name).</summary>
    public List<string> ValueNames { get; private set; } = new() { "Exists" };

    /// <summary>Maps value names to their current data for auto-filling the Value field.</summary>
    public Dictionary<string, string> ValueData { get; private set; } = new();

    public RegistryBrowserDialog()
    {
        InitializeComponent();
        _roots = RegistryTreeNode.CreateRoots();
        KeyTree.ItemsSource = _roots;
    }

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem tvi) return;
        if (tvi.DataContext is not RegistryTreeNode node) return;
        if (node.IsLoaded) return;

        var prevCursor = Cursor;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            node.LoadChildren();
        }
        finally
        {
            Cursor = prevCursor;
        }
    }

    private void KeyTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not RegistryTreeNode node) return;
        if (node.Name == "_dummy_") return;

        CurrentKeyPath.Text = node.FullPath;
        PathBar.Text = node.FullPath;
        LoadValues(node);

        // Default: key-level selection with "Exists" property
        SelectedPath = node.FullPath;
        SelectedProperty = "Exists";
        HasSelection = true;
        ValuesGrid.SelectedItem = null;
        UpdateSelectionSummary();
    }

    private void LoadValues(RegistryTreeNode node)
    {
        ValuesGrid.ItemsSource = null;
        ValuesPlaceholder.Visibility = Visibility.Collapsed;
        AccessDeniedText.Visibility = Visibility.Collapsed;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(node.Hive, RegistryView.Registry64);
            using var key = string.IsNullOrEmpty(node.SubKeyPath)
                ? baseKey
                : baseKey.OpenSubKey(node.SubKeyPath, writable: false);

            if (key is null)
            {
                AccessDeniedText.Visibility = Visibility.Visible;
                return;
            }

            var items = new ObservableCollection<RegistryValueDisplay>();
            var valueNames = key.GetValueNames();

            foreach (var name in valueNames)
            {
                try
                {
                    var kind = key.GetValueKind(name);
                    var data = key.GetValue(name);
                    var displayName = string.IsNullOrEmpty(name) ? "(Default)" : name;

                    items.Add(new RegistryValueDisplay
                    {
                        Name = displayName,
                        Type = FormatValueKind(kind),
                        Data = FormatRegistryData(data, kind),
                        IsDefault = string.IsNullOrEmpty(name)
                    });
                }
                catch
                {
                    // Skip values that cannot be read
                }
            }

            ValuesGrid.ItemsSource = items;

            ValueNames = new List<string> { "Exists" };
            ValueData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Exists"] = "True"
            };
            foreach (var name in valueNames)
            {
                try
                {
                    var displayName = string.IsNullOrEmpty(name) ? "(Default)" : name;
                    var data = key.GetValue(name);
                    ValueNames.Add(displayName);
                    ValueData[displayName] = FormatDetectionValue(data, key.GetValueKind(name));
                }
                // must-stay-silent: a single registry value can fail to read
                // (permission/locked) without invalidating the whole key
                // browse. Skipping the offending value lets the rest of the
                // key render; logging would noise a list of similar misses.
                catch { }
            }

            if (items.Count == 0)
            {
                ValuesPlaceholder.Text = "This key has no values";
                ValuesPlaceholder.Visibility = Visibility.Visible;
            }
        }
        catch (SecurityException)
        {
            AccessDeniedText.Visibility = Visibility.Visible;
        }
        catch (UnauthorizedAccessException)
        {
            AccessDeniedText.Visibility = Visibility.Visible;
        }
    }

    private void ValuesGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // The DataGrid's internal ScrollViewer doesn't receive wheel events when
        // hosted inside a ContentDialog. Forward them manually.
        if (sender is not DataGrid dg) return;
        var sv = FindVisualChild<ScrollViewer>(dg);
        if (sv is null) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void ValuesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ValuesGrid.SelectedItem is not RegistryValueDisplay item) return;

        SelectedProperty = item.IsDefault ? "(Default)" : item.Name;
        HasSelection = true;
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        if (!HasSelection || SelectedPath is null)
        {
            SelectionSummary.Text = "(none)";
            return;
        }

        SelectionSummary.Text = SelectedProperty == "Exists"
            ? $"{SelectedPath}  [Key Exists]"
            : $"{SelectedPath} -> {SelectedProperty}";
    }

    private void PathBar_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true; // Prevent ContentDialog from treating Enter as primary button
            NavigateToPath(PathBar.Text.Trim());
        }
    }

    private void GoButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPath(PathBar.Text.Trim());
    }

    private void Shortcut64_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPath(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    private void Shortcut32_Click(object sender, RoutedEventArgs e)
    {
        NavigateToPath(@"HKLM:\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    private void ShowNavError(string message)
    {
        NavFeedback.Text = message;
        NavFeedback.Visibility = Visibility.Visible;
    }

    private void ClearNavError()
    {
        NavFeedback.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Normalizes a registry path from any accepted format into (hivePrefix, remainder).
    /// Accepted formats:
    ///   HKLM:\path, HKCU:\path           (PowerShell)
    ///   HKLM\path, HKCU\path             (short)
    ///   HKEY_LOCAL_MACHINE\path           (regedit)
    ///   HKEY_CURRENT_USER\path            (regedit)
    /// Returns null if the format is not recognized.
    /// </summary>
    private static (string Hive, string Remainder)? ParseRegistryPath(string path)
    {
        path = path.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(path)) return null;

        // Try each known prefix, longest first to avoid ambiguity
        var prefixes = new (string Prefix, string Hive)[]
        {
            ("HKEY_LOCAL_MACHINE:\\", "HKLM"),
            ("HKEY_LOCAL_MACHINE/",  "HKLM"),
            ("HKEY_LOCAL_MACHINE\\", "HKLM"),
            ("HKEY_CURRENT_USER:\\", "HKCU"),
            ("HKEY_CURRENT_USER/",  "HKCU"),
            ("HKEY_CURRENT_USER\\", "HKCU"),
            ("HKLM:\\",             "HKLM"),
            ("HKLM:/",              "HKLM"),
            ("HKLM\\",              "HKLM"),
            ("HKCU:\\",             "HKCU"),
            ("HKCU:/",              "HKCU"),
            ("HKCU\\",              "HKCU"),
        };

        foreach (var (prefix, hive) in prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (hive, path.Substring(prefix.Length));
        }

        // Bare hive name with no trailing separator
        var bareNames = new (string Name, string Hive)[]
        {
            ("HKEY_LOCAL_MACHINE", "HKLM"),
            ("HKEY_CURRENT_USER",  "HKCU"),
            ("HKLM:",              "HKLM"),
            ("HKCU:",              "HKCU"),
            ("HKLM",               "HKLM"),
            ("HKCU",               "HKCU"),
        };

        foreach (var (name, hive) in bareNames)
        {
            if (path.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (hive, "");
        }

        return null;
    }

    private void NavigateToPath(string path)
    {
        ClearNavError();

        var parsed = ParseRegistryPath(path);
        if (parsed is null)
        {
            ShowNavError("Invalid format. Use HKLM:\\... or HKEY_LOCAL_MACHINE\\...");
            return;
        }

        var (hivePrefix, remainder) = parsed.Value;

        RegistryTreeNode? currentNode = null;
        foreach (var root in _roots)
        {
            if (root.Name.Equals(hivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                currentNode = root;
                break;
            }
        }
        if (currentNode is null) return;

        currentNode.LoadChildren();
        var currentContainer = KeyTree.ItemContainerGenerator.ContainerFromItem(currentNode) as TreeViewItem;
        if (currentContainer is null) return;
        currentContainer.IsExpanded = true;
        KeyTree.UpdateLayout();

        // If just the hive root, select and expand it
        if (string.IsNullOrEmpty(remainder))
        {
            currentContainer.IsSelected = true;
            PathBar.Text = currentNode.FullPath;
            var rootContainer = currentContainer;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                ScrollToTop(rootContainer);
            });
            return;
        }

        var segments = remainder.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            currentNode.LoadChildren();

            if (currentNode.IsAccessDenied)
            {
                ShowNavError($"Access denied: {currentNode.FullPath}");
                PathBar.Text = currentNode.FullPath;
                return;
            }

            RegistryTreeNode? childNode = null;
            foreach (var child in currentNode.Children)
            {
                if (child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    childNode = child;
                    break;
                }
            }
            if (childNode is null)
            {
                ShowNavError($"Key not found: ...\\{segment}");
                PathBar.Text = currentNode.FullPath;
                return;
            }

            currentContainer!.IsExpanded = true;
            currentContainer.UpdateLayout();

            var childContainer = currentContainer.ItemContainerGenerator.ContainerFromItem(childNode)
                                 as TreeViewItem;
            if (childContainer is null)
            {
                KeyTree.UpdateLayout();
                childContainer = currentContainer.ItemContainerGenerator.ContainerFromItem(childNode)
                                 as TreeViewItem;
            }
            if (childContainer is null) return;

            currentNode = childNode;
            currentContainer = childContainer;
        }

        currentContainer!.IsExpanded = true;
        currentContainer.UpdateLayout();
        currentContainer.IsSelected = true;
        PathBar.Text = currentNode!.FullPath;

        // Scroll the selected item to the top of the TreeView after layout settles
        var container = currentContainer;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ScrollToTop(container);
        });
    }

    /// <summary>
    /// Scrolls a TreeViewItem to the top of the TreeView's visible area.
    /// </summary>
    private void ScrollToTop(TreeViewItem item)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(KeyTree);
        if (scrollViewer is null)
        {
            item.BringIntoView();
            return;
        }

        var transform = item.TransformToAncestor(scrollViewer);
        var itemPosition = transform.Transform(new System.Windows.Point(0, 0));
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + itemPosition.Y);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    private static string FormatValueKind(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String       => "REG_SZ",
        RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
        RegistryValueKind.Binary       => "REG_BINARY",
        RegistryValueKind.DWord        => "REG_DWORD",
        RegistryValueKind.MultiString  => "REG_MULTI_SZ",
        RegistryValueKind.QWord        => "REG_QWORD",
        RegistryValueKind.None         => "REG_NONE",
        _                              => kind.ToString()
    };

    private static string FormatRegistryData(object? data, RegistryValueKind kind)
    {
        if (data is null) return "(not set)";

        return kind switch
        {
            RegistryValueKind.Binary when data is byte[] bytes
                => BitConverter.ToString(bytes).Replace("-", " "),
            RegistryValueKind.MultiString when data is string[] arr
                => string.Join(" | ", arr),
            RegistryValueKind.DWord when data is int intVal
                => $"0x{intVal:X8} ({intVal})",
            RegistryValueKind.QWord when data is long longVal
                => $"0x{longVal:X16} ({longVal})",
            _ => data.ToString() ?? "(not set)"
        };
    }

    /// <summary>
    /// Formats a registry value for detection script comparison (clean values, no hex prefixes).
    /// </summary>
    private static string FormatDetectionValue(object? data, RegistryValueKind kind)
    {
        if (data is null) return string.Empty;

        return kind switch
        {
            RegistryValueKind.DWord when data is int intVal => intVal.ToString(),
            RegistryValueKind.QWord when data is long longVal => longVal.ToString(),
            RegistryValueKind.Binary when data is byte[] bytes
                => BitConverter.ToString(bytes).Replace("-", " "),
            RegistryValueKind.MultiString when data is string[] arr
                => string.Join(" | ", arr),
            _ => data.ToString() ?? string.Empty
        };
    }

    // ================================================================
    // Uninstall search
    // ================================================================

    private static readonly string[] UninstallKeyPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            RunUninstallSearch();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // When the search box is cleared, return to tree view
        if (string.IsNullOrEmpty(SearchBox.Text))
        {
            ShowTreeView();
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        RunUninstallSearch();
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ShowTreeView();
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not UninstallSearchResult result) return;

        // Navigate to the selected key in the tree and switch back to tree view
        ShowTreeView();
        NavigateToPath(result.FullPath);
    }

    private void ShowTreeView()
    {
        SearchResults.Visibility = Visibility.Collapsed;
        SearchStatus.Visibility = Visibility.Collapsed;
        KeyTree.Visibility = Visibility.Visible;
    }

    private void ShowSearchResults()
    {
        KeyTree.Visibility = Visibility.Collapsed;
        SearchStatus.Visibility = Visibility.Collapsed;
        SearchResults.Visibility = Visibility.Visible;
    }

    private void ShowSearchStatus(string message)
    {
        KeyTree.Visibility = Visibility.Collapsed;
        SearchResults.Visibility = Visibility.Collapsed;
        SearchStatus.Text = message;
        SearchStatus.Visibility = Visibility.Visible;
    }

    private void RunUninstallSearch()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ShowTreeView();
            return;
        }

        var results = new List<UninstallSearchResult>();

        foreach (var keyPath in UninstallKeyPaths)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using var uninstallKey = baseKey.OpenSubKey(keyPath, writable: false);
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = uninstallKey.OpenSubKey(subKeyName, writable: false);
                        if (sub is null) continue;

                        var displayName = sub.GetValue("DisplayName") as string ?? "";
                        var version = sub.GetValue("DisplayVersion") as string ?? "";
                        var publisher = sub.GetValue("Publisher") as string ?? "";

                        // Match query against subkey name and DisplayName (case-insensitive contains)
                        bool matches = displayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                    || subKeyName.Contains(query, StringComparison.OrdinalIgnoreCase);

                        if (!matches) continue;

                        var fullPath = $@"HKLM:\{keyPath}\{subKeyName}";
                        results.Add(new UninstallSearchResult
                        {
                            DisplayName = string.IsNullOrEmpty(displayName) ? subKeyName : displayName,
                            Version = version,
                            Publisher = publisher,
                            KeyName = subKeyName,
                            FullPath = fullPath,
                        });
                    }
                    catch
                    {
                        // Skip subkeys that cannot be read
                    }
                }
            }
            catch
            {
                // Skip entire hive path if inaccessible
            }
        }

        if (results.Count == 0)
        {
            ShowSearchStatus($"No uninstall entries matching \"{query}\"");
            return;
        }

        results.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        SearchResults.ItemsSource = results;
        ShowSearchResults();
    }
}

/// <summary>Simple display model for a registry value row in the DataGrid.</summary>
internal class RegistryValueDisplay
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Data { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}

/// <summary>Display model for an uninstall registry search result.</summary>
internal class UninstallSearchResult
{
    public string DisplayName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string KeyName { get; init; } = string.Empty;
    public bool HasPublisher => !string.IsNullOrEmpty(Publisher);
    /// <summary>Full registry path in PowerShell format.</summary>
    public string FullPath { get; init; } = string.Empty;
}
