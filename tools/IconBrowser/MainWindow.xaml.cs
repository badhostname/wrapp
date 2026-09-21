using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace IconBrowser;

public partial class MainWindow : Window
{
    private List<IconEntry> _mdl2All = new();
    private List<IconEntry> _wpfUiAll = new();
    private List<IconEntry> _filtered = new();
    private readonly DispatcherTimer _debounce;
    private const int Columns = 8;

    public MainWindow()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RebuildFiltered(); };

        Loaded += (_, _) =>
        {
            Mouse.OverrideCursor = Cursors.Wait;
            // Defer heavy work so the window paints first
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                LoadMdl2Icons();
                LoadWpfUiIcons();
                RebuildFiltered();
                Mouse.OverrideCursor = null;
            });
        };
    }

    // -----------------------------------------------------------------------
    // Data loading
    // -----------------------------------------------------------------------

    private void LoadMdl2Icons()
    {
        var font = new FontFamily("Segoe MDL2 Assets");
        foreach (var (name, code) in GetMdl2Glyphs())
        {
            var hex = code.ToString("X4");
            _mdl2All.Add(new IconEntry
            {
                Name = name,
                Glyph = char.ConvertFromUtf32(code),
                CodeHex = hex,
                CopyText = $"&#x{hex};",
                TooltipText = $"{name}\nXAML: &#x{hex};\nC#: \"\\u{hex}\"\nU+{hex}",
                Font = font,
                Source = "MDL2",
            });
        }
    }

    private void LoadWpfUiIcons()
    {
        foreach (var symbol in Enum.GetValues<SymbolRegular>())
        {
            if ((int)symbol == 0) continue;
            var name = symbol.ToString();
            var code = (int)symbol;
            _wpfUiAll.Add(new IconEntry
            {
                Name = name,
                Symbol = symbol,
                CodeHex = code.ToString("X4"),
                CopyText = name,
                TooltipText = $"{name}\n<ui:SymbolIcon Symbol=\"{name}\"/>\nSymbolRegular.{name}\n0x{code:X4}",
                Source = "WpfUi",
            });
        }
    }

    // -----------------------------------------------------------------------
    // Filtering -- produces row-grouped data for the virtualizing list
    // -----------------------------------------------------------------------

    private void RebuildFiltered()
    {
        var isMdl2 = TabMdl2.IsChecked == true;
        var source = isMdl2 ? _mdl2All : _wpfUiAll;
        var query = SearchBox.Text?.Trim() ?? "";

        _filtered = string.IsNullOrEmpty(query)
            ? new List<IconEntry>(source)
            : source.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || i.CodeHex.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        // Group into rows for the virtualizing stack panel
        var rows = new List<IconRow>();
        for (int i = 0; i < _filtered.Count; i += Columns)
        {
            var row = new IconRow { IsMdl2 = isMdl2 };
            for (int j = i; j < Math.Min(i + Columns, _filtered.Count); j++)
                row.Items.Add(_filtered[j]);
            rows.Add(row);
        }

        IconList.ItemTemplate = isMdl2 ? BuildMdl2RowTemplate() : BuildWpfUiRowTemplate();
        IconList.ItemsSource = rows;

        CountLabel.Text = $"{_filtered.Count} / {source.Count}";
    }

    // -----------------------------------------------------------------------
    // Row templates (built once per tab switch, reused for all rows)
    // -----------------------------------------------------------------------

    private DataTemplate BuildMdl2RowTemplate()
    {
        // Each row is a horizontal StackPanel of icon tiles
        var template = new DataTemplate(typeof(IconRow));
        var factory = new FrameworkElementFactory(typeof(ItemsControl));
        factory.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Items"));

        var panelTemplate = new ItemsPanelTemplate();
        var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
        panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panelTemplate.VisualTree = panelFactory;
        factory.SetValue(ItemsControl.ItemsPanelProperty, panelTemplate);

        var itemTemplate = new DataTemplate(typeof(IconEntry));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.WidthProperty, 120.0);
        border.SetValue(Border.HeightProperty, 80.0);
        border.SetValue(Border.MarginProperty, new Thickness(3));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)));
        border.SetValue(Border.CursorProperty, Cursors.Hand);
        border.SetBinding(Border.ToolTipProperty, new System.Windows.Data.Binding("TooltipText"));
        border.AddHandler(Border.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnIconClick));

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
        stack.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);

        var glyph = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        glyph.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Glyph"));
        glyph.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 24.0);
        glyph.SetBinding(System.Windows.Controls.TextBlock.FontFamilyProperty, new System.Windows.Data.Binding("Font"));
        glyph.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)));
        glyph.SetValue(System.Windows.Controls.TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(System.Windows.Controls.TextBlock.MarginProperty, new Thickness(0, 0, 0, 4));

        var name = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        name.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        name.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 9.0);
        name.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)));
        name.SetValue(System.Windows.Controls.TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        name.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(System.Windows.Controls.TextBlock.MaxWidthProperty, 110.0);
        name.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Center);

        var code = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        code.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("CodeHex"));
        code.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 8.0);
        code.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));
        code.SetValue(System.Windows.Controls.TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);

        stack.AppendChild(glyph);
        stack.AppendChild(name);
        stack.AppendChild(code);
        border.AppendChild(stack);
        itemTemplate.VisualTree = border;

        factory.SetValue(ItemsControl.ItemTemplateProperty, itemTemplate);
        template.VisualTree = factory;
        return template;
    }

    private DataTemplate BuildWpfUiRowTemplate()
    {
        var template = new DataTemplate(typeof(IconRow));
        var factory = new FrameworkElementFactory(typeof(ItemsControl));
        factory.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Items"));

        var panelTemplate = new ItemsPanelTemplate();
        var panelFactory = new FrameworkElementFactory(typeof(StackPanel));
        panelFactory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panelTemplate.VisualTree = panelFactory;
        factory.SetValue(ItemsControl.ItemsPanelProperty, panelTemplate);

        var itemTemplate = new DataTemplate(typeof(IconEntry));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.WidthProperty, 120.0);
        border.SetValue(Border.HeightProperty, 80.0);
        border.SetValue(Border.MarginProperty, new Thickness(3));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)));
        border.SetValue(Border.CursorProperty, Cursors.Hand);
        border.SetBinding(Border.ToolTipProperty, new System.Windows.Data.Binding("TooltipText"));
        border.AddHandler(Border.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnIconClick));

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
        stack.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);

        var icon = new FrameworkElementFactory(typeof(SymbolIcon));
        icon.SetBinding(SymbolIcon.SymbolProperty, new System.Windows.Data.Binding("Symbol"));
        icon.SetValue(SymbolIcon.FontSizeProperty, 24.0);
        icon.SetValue(SymbolIcon.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)));
        icon.SetValue(SymbolIcon.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        icon.SetValue(SymbolIcon.MarginProperty, new Thickness(0, 0, 0, 4));

        var name = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        name.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        name.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 8.0);
        name.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)));
        name.SetValue(System.Windows.Controls.TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        name.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(System.Windows.Controls.TextBlock.MaxWidthProperty, 110.0);
        name.SetValue(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Center);

        var code = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        code.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("CodeHex"));
        code.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 7.0);
        code.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));
        code.SetValue(System.Windows.Controls.TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);

        stack.AppendChild(icon);
        stack.AppendChild(name);
        stack.AppendChild(code);
        border.AppendChild(stack);
        itemTemplate.VisualTree = border;

        factory.SetValue(ItemsControl.ItemTemplateProperty, itemTemplate);
        template.VisualTree = factory;
        return template;
    }

    // -----------------------------------------------------------------------
    // Click handling
    // -----------------------------------------------------------------------

    private void OnIconClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.DataContext is not IconEntry entry) return;

        try
        {
            Clipboard.SetText(entry.CopyText);
            StatusText.Text = entry.Source == "MDL2"
                ? $"Copied: {entry.CopyText}  |  {entry.Name}  |  U+{entry.CodeHex}"
                : $"Copied: {entry.CopyText}  |  <ui:SymbolIcon Symbol=\"{entry.Name}\"/>  |  0x{entry.CodeHex}";
        }
        catch
        {
            StatusText.Text = $"Selected: {entry.Name}";
        }
    }

    // -----------------------------------------------------------------------
    // UI events
    // -----------------------------------------------------------------------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RebuildFiltered();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void IconList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection handled by OnIconClick instead
        IconList.SelectedItem = null;
    }

    // -----------------------------------------------------------------------
    // MDL2 glyph catalog
    // -----------------------------------------------------------------------

    private static List<(string Name, int Code)> GetMdl2Glyphs()
    {
        return new List<(string, int)>
        {
            ("GlobalNavButton", 0xE700), ("Wifi", 0xE701), ("Bluetooth", 0xE702),
            ("Connect", 0xE703), ("InternetSharing", 0xE704), ("VPN", 0xE705),
            ("Brightness", 0xE706), ("MapPin", 0xE707), ("QuietHours", 0xE708),
            ("Airplane", 0xE709), ("Tablet", 0xE70A), ("QuickNote", 0xE70B),
            ("RememberedDevice", 0xE70C), ("ChevronLeft", 0xE70D), ("ChevronRight", 0xE70E),
            ("ChevronUp", 0xE70F), ("ChevronDown", 0xE710 - 1 + 1),
            ("Add", 0xE710), ("Cancel", 0xE711), ("More", 0xE712),
            ("Setting", 0xE713), ("Video", 0xE714), ("Mail", 0xE715),
            ("People", 0xE716), ("Phone", 0xE717), ("Pin", 0xE718),
            ("Shop", 0xE719), ("Stop", 0xE71A), ("Link", 0xE71B),
            ("Filter", 0xE71C), ("AllApps", 0xE71D), ("Zoom", 0xE71E),
            ("ZoomOut", 0xE71F), ("Microphone", 0xE720), ("Search", 0xE721),
            ("Camera", 0xE722), ("Attach", 0xE723),
            ("Back", 0xE72B), ("Forward", 0xE72A), ("Refresh", 0xE72C),
            ("Share", 0xE72D), ("Lock", 0xE72E),
            ("Blocked", 0xE733), ("Favorite", 0xE734), ("FavoriteStar", 0xE735),
            ("ReadingMode", 0xE736), ("CheckboxComposite", 0xE73A), ("CheckMark", 0xE73E),
            ("ExitFullScreen", 0xE73F), ("FullScreen", 0xE740),
            ("Up", 0xE74A), ("Down", 0xE74B), ("Delete", 0xE74D), ("Save", 0xE74E),
            ("Mute", 0xE74F), ("CommandPrompt", 0xE756),
            ("PageLeft", 0xE760), ("PageRight", 0xE761),
            ("Volume", 0xE767), ("Play", 0xE768), ("Pause", 0xE769),
            ("ChevronLeft2", 0xE76B), ("ChevronRight2", 0xE76C),
            ("Personalize", 0xE771), ("Globe", 0xE774), ("TimeLanguage", 0xE775),
            ("Ease", 0xE776), ("UpdateRestore", 0xE777), ("ContactInfo", 0xE779),
            ("Unpin", 0xE77A), ("Contact", 0xE77B), ("Paste", 0xE77F),
            ("Unlock", 0xE785), ("Calendar", 0xE787),
            ("NewWindow", 0xE78B), ("SaveLocal", 0xE78C), ("SaveAs", 0xE792),
            ("OpenWith", 0xE7AC),
            ("Edit", 0xE70F + 3), ("Redo", 0xE7A6), ("Undo", 0xE7A7),
            ("Package", 0xE7B8), ("Warning", 0xE7BA), ("Flag", 0xE7C1),
            ("Move", 0xE7C2), ("TouchPointer", 0xE7C9),
            ("TVMonitor", 0xE7F4), ("Laptop", 0xE7F8), ("PowerButton", 0xE7E8),
            ("Home", 0xE80F), ("History", 0xE81C), ("MapLayers", 0xE81E),
            ("Work", 0xE821), ("Clock", 0xE823), ("FolderOpen", 0xE838),
            ("Ethernet", 0xE839), ("Deploy", 0xE83B), ("Shield", 0xE83D),
            ("Sticky", 0xE840),
            ("Cloud", 0xE753), ("Site", 0xE770),
            ("Cellular", 0xE870),
            ("View", 0xE890), ("Previous", 0xE892), ("Next", 0xE893),
            ("Sync", 0xE895), ("Download", 0xE896), ("Help", 0xE897),
            ("Upload", 0xE898), ("MailForward", 0xE89C), ("ClosePane", 0xE89F),
            ("ZoomIn", 0xE8A3), ("Bookmarks", 0xE8A4), ("Document", 0xE8A5),
            ("ProtectedDocument", 0xE8A6), ("OpenInNewWindow", 0xE8A7),
            ("OrgChart", 0xE8A9), ("Rename", 0xE8AC), ("Go", 0xE8AD),
            ("SelectAll", 0xE8B3), ("Import", 0xE8B5), ("ImportAll", 0xE8B6),
            ("Folder", 0xE8B7), ("Photo", 0xE8B9),
            ("CalendarDay", 0xE8BF), ("CalendarWeek", 0xE8C0), ("Characters", 0xE8C1),
            ("Cut", 0xE8C6), ("Copy", 0xE8C8), ("Sort", 0xE8CB),
            ("MailReply", 0xE8CA), ("DisconnectDrive", 0xE8CD), ("MapDrive", 0xE8CE),
            ("TextFont", 0xE8D2), ("FontColor", 0xE8D3), ("Contact2", 0xE8D4),
            ("Permissions", 0xE8D7), ("Italic", 0xE8DB), ("Underline", 0xE8DC),
            ("Bold", 0xE8DD), ("MoveToFolder", 0xE8DE),
            ("OpenFile", 0xE8E5), ("PhoneMobile", 0xE8EA), ("Tag", 0xE8EC),
            ("Library", 0xE8F1), ("NewFolder", 0xE8F4), ("BlockContact", 0xE8F8),
            ("Accept", 0xE8FB), ("AddFriend", 0xE8FA), ("BulletedList", 0xE8FD),
            ("Manage", 0xE912), ("Print3D", 0xE914),
            ("DockLeft", 0xE90C), ("DockRight", 0xE90D), ("Repair", 0xE90F),
            ("ActionCenter", 0xE91C), ("Completed", 0xE930),
            ("Handwriting", 0xE929), ("Streaming", 0xE93E),
            ("Info", 0xE946), ("Code", 0xE943),
            ("ChevronUpSmall", 0xE96D), ("ChevronDownSmall", 0xE96E),
            ("ChevronLeftSmall", 0xE96F), ("ChevronRightSmall", 0xE970),
            ("DropDown", 0xE972), ("ExpandTile", 0xE976), ("PC1", 0xE977),
            ("Network", 0xE968), ("USB", 0xE88E), ("Print", 0xE749),
            ("Diagnostic", 0xE9D9), ("Processing", 0xE9F5),
            ("AppsList", 0xEA37), ("Error", 0xEA39), ("StatusCircle", 0xEA3B),
            ("StatusTriangle", 0xEA21), ("ShieldCheck", 0xEA1A),
            ("Certificate", 0xEB95), ("Bug", 0xEBE8), ("Family", 0xEBDA),
            ("Tiles", 0xECA5), ("DeveloperTools", 0xEC7A),
            ("Export", 0xEDE1), ("ExportMirrored", 0xEDE2),
            ("Print", 0xE749),
        };
    }
}

// -----------------------------------------------------------------------
// Models
// -----------------------------------------------------------------------

public class IconEntry
{
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public SymbolRegular Symbol { get; init; }
    public string CodeHex { get; init; } = "";
    public string CopyText { get; init; } = "";
    public string TooltipText { get; init; } = "";
    public FontFamily? Font { get; init; }
    public string Source { get; init; } = "";
}

public class IconRow
{
    public List<IconEntry> Items { get; } = new();
    public bool IsMdl2 { get; init; }
}
