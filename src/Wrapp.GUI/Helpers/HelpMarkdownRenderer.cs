using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Wrapp.Helpers;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TextBox = System.Windows.Controls.TextBox;

namespace Wrapp;

/// <summary>
/// Converts Markdown help strings into themed WPF FlowDocument panels.
/// Purpose-built Markdown renderer - supports the subset of Markdown
/// used in HelpContent.xaml (headings, bold, italic, inline code, fenced
/// code blocks with syntax highlighting, bullet lists, pipe tables,
/// blockquotes, links) and emits every element directly so there are no
/// detection heuristics or post-processing walks.
/// </summary>
public static partial class HelpMarkdownRenderer
{
    // ── Theme brush bag ─────────────────────────────────────────────
    private record ThemeBrushes(
        Brush Primary, Brush Secondary, Brush Accent, Brush AccentBg,
        Brush Border, Brush InputBg, Brush TableHeaderBg, Brush RowAlt,
        Brush Keyword, Brush String, Brush CmdletHighlight, Brush ButtonBg,
        Brush NavIcon);

    // ── Icon + button glyph maps ────────────────────────────────────

    // The Fluent icon font bundled inside the Wpf.Ui assembly. Stable
    // across wpf-ui versions as long as the assembly is referenced.
    // Lazily created on first use - the pack:// URI resolver isn't
    // registered until a WPF Application has been instantiated, so
    // eager initialisation would crash anything that touches this
    // class before the UI is up (including unit tests).
    private static readonly Lazy<System.Windows.Media.FontFamily> _fluentIconFont =
        new(() => new System.Windows.Media.FontFamily(
            new Uri("pack://application:,,,/Wpf.Ui;component/Resources/Fonts/", UriKind.Absolute),
            "./#FluentSystemIcons-Regular"));
    private static System.Windows.Media.FontFamily FluentIconFont => _fluentIconFont.Value;

    private static readonly Dictionary<string, Wpf.Ui.Controls.SymbolRegular> IconMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cloud24"]                    = Wpf.Ui.Controls.SymbolRegular.Cloud24,
        ["server24"]                   = Wpf.Ui.Controls.SymbolRegular.Server24,
        ["eyetracking20"]              = Wpf.Ui.Controls.SymbolRegular.EyeTracking20,
        ["codetext20"]                 = Wpf.Ui.Controls.SymbolRegular.CodeText20,
        ["codetextedit20"]             = Wpf.Ui.Controls.SymbolRegular.CodeTextEdit20,
        ["boxarrowup24"]               = Wpf.Ui.Controls.SymbolRegular.BoxArrowUp24,
        ["appslist24"]                 = Wpf.Ui.Controls.SymbolRegular.AppsList24,
        ["wrench24"]                   = Wpf.Ui.Controls.SymbolRegular.Wrench24,
        ["settings24"]                 = Wpf.Ui.Controls.SymbolRegular.Settings24,
        ["home24"]                     = Wpf.Ui.Controls.SymbolRegular.Home24,
        ["history24"]                  = Wpf.Ui.Controls.SymbolRegular.History24,
        ["textbulletlistsquareclock20"] = Wpf.Ui.Controls.SymbolRegular.TextBulletListSquareClock20,
        ["rename24"]                   = Wpf.Ui.Controls.SymbolRegular.Rename24,
    };

    private static readonly Dictionary<string, string> ButtonGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Add"]              = "\uE710",
        ["Remove"]           = "\uE738",
        ["Duplicate"]        = "\uE8C8",
        ["Assignments"]      = "\uE716",
        ["Deployments"]      = "\uE716",
        ["Refresh"]          = "\uE72C",
        ["Filter"]           = "\uE71C",
        ["History"]          = "\uE81C",
        ["Browse"]           = "\uE8B7",
        ["Save"]             = "\uE74E",
        ["Save As"]          = "\uE792",
        ["Apply"]            = "\uE73E",
        ["Apply Changes"]    = "\uE73E",
        ["Sync Domains"]     = "\uE895",
        ["Start"]            = "\uE768",
        ["Cancel"]           = "\uE711",
        ["Open Log Folder"]  = "\uE838",
    };

    private static readonly Dictionary<string, string> BadgeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"]     = "#C62828",
        ["green"]   = "#2E7D32",
        ["blue"]    = "#1565C0",
        ["amber"]   = "#EF6C00",
        ["orange"]  = "#FF9800",
        ["gray"]    = "#616161",
        ["magenta"] = "#AD1457",
        ["purple"]  = "#6C5FC7",
        ["slate"]   = "#455A64",
        ["lgray"]   = "#8E8E8E",
    };

    // ── Block tokens ────────────────────────────────────────────────
    // `internal` rather than `private` so the test assembly can assert
    // on tokenizer output without us having to round-trip through a
    // FlowDocument and re-parse shape.
    internal abstract record MdBlock;
    internal record HeadingBlock(int Level, string Text) : MdBlock;
    internal record ParagraphBlock(string Text) : MdBlock;
    internal record CodeBlock(string Language, string Code) : MdBlock;
    internal record TableBlock(List<string[]> Rows) : MdBlock;
    internal record ListBlock(List<string> Items, bool Ordered) : MdBlock;
    internal record QuoteBlock(string Text) : MdBlock;
    internal record HorizontalRuleBlock : MdBlock;

    // ── Public entry point ──────────────────────────────────────────

    public static StackPanel Render(string markdown, FrameworkElement resourceSource)
    {
        var brushes = LoadBrushes(resourceSource);
        var preprocessed = PreprocessLegacyFieldLines(markdown);
        var blocks = Tokenize(preprocessed);
        var flowDoc = BuildDocument(blocks, brushes);

        var viewer = new FlowDocumentScrollViewer
        {
            Document = flowDoc,
            MaxWidth = 780,
            IsToolBarVisible = false,
            IsSelectionEnabled = true,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // Side padding so body text and code cards don't crowd
            // the dialog scrollbar / chrome.
            Padding = new Thickness(14, 4, 14, 8),
        };
        ScrollBubbling.SetBubbleScroll(viewer, true);

        var panel = new StackPanel { MaxWidth = 780 };
        panel.Children.Add(viewer);
        return panel;
    }

    // ── Brush loading ───────────────────────────────────────────────

    private static ThemeBrushes LoadBrushes(FrameworkElement src)
    {
        Brush B(string key, byte r, byte g, byte b) =>
            src.TryFindResource(key) as Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

        return new ThemeBrushes(
            Primary:          B("TextPrimaryBrush",       0xFF, 0xFF, 0xFF),
            Secondary:        B("TextSecondaryBrush",     0xA0, 0xA0, 0xA0),
            Accent:           B("AccentBrush",            0x9A, 0xC9, 0xCF),
            AccentBg:         B("AccentBgBrush",          0x6B, 0xB5, 0xE0),
            Border:           B("AppBorderBrush",         0x2A, 0x35, 0x38),
            InputBg:          B("InputBgBrush",           0x33, 0x3B, 0x3D),
            TableHeaderBg:    B("TableHeaderBgBrush",     0x21, 0x29, 0x2C),
            RowAlt:           B("RowAltBrush",            0x1B, 0x24, 0x27),
            Keyword:          B("RunningBrush",           0x42, 0xA5, 0xF5),
            String:           B("ConnectedBrush",         0x4C, 0xAF, 0x50),
            CmdletHighlight:  B("CommandFieldAccentBrush",0x6B, 0xB5, 0xE0),
            ButtonBg:         B("NormalBtnBgBrush",       0x21, 0x29, 0x2C),
            NavIcon:          B("TextMutedBrush",         0xFF, 0xFF, 0xFF));
    }

    // Matches `<digits>. text` - the standard Markdown numbered-list marker.
}