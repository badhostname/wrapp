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

public static partial class HelpMarkdownRenderer
{
    // -----------------------------------------------------------------------
    // Block-level FlowDocument builders -- one method per MdBlock variant.
    // Consumed from BuildDocument inside this same file.
    // -----------------------------------------------------------------------

    private static FlowDocument BuildDocument(List<MdBlock> blocks, ThemeBrushes b)
    {
        var doc = new FlowDocument
        {
            Foreground  = b.Primary,
            FontFamily  = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize    = 13,
            PagePadding = new Thickness(0),
            Background  = System.Windows.Media.Brushes.Transparent,
        };

        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    doc.Blocks.Add(BuildHeading(h, b));
                    break;
                case ParagraphBlock p:
                    doc.Blocks.Add(BuildParagraph(p.Text, b));
                    break;
                case CodeBlock c:
                    doc.Blocks.Add(BuildCodeBlock(c, b));
                    break;
                case TableBlock t:
                    doc.Blocks.Add(BuildTable(t, b));
                    break;
                case ListBlock l:
                    doc.Blocks.Add(BuildList(l, b));
                    break;
                case QuoteBlock q:
                    doc.Blocks.Add(BuildQuote(q, b));
                    break;
                case HorizontalRuleBlock:
                    doc.Blocks.Add(BuildHorizontalRule(b));
                    break;
            }
        }

        return doc;
    }

    // ── Heading ─────────────────────────────────────────────────────

    private static Paragraph BuildHeading(HeadingBlock h, ThemeBrushes b)
    {
        double fontSize = h.Level switch { 1 => 22d, 2 => 18d, _ => 15d };
        var para = new Paragraph
        {
            FontSize   = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = b.Primary,
            Margin     = h.Level switch
            {
                1 => new Thickness(0, 0, 0, 8),
                2 => new Thickness(0, 10, 0, 4),
                _ => new Thickness(0, 6, 0, 4),
            },
        };
        if (h.Level <= 2)
        {
            para.BorderBrush     = h.Level == 1 ? b.Accent : b.Border;
            para.BorderThickness = new Thickness(0, 0, 0, h.Level == 1 ? 2 : 1);
            para.Padding         = new Thickness(0, 0, 0, h.Level == 1 ? 6 : 4);
        }
        para.Inlines.AddRange(ParseInlines(h.Text, b));
        return para;
    }

    // ── Paragraph ───────────────────────────────────────────────────

    private static Paragraph BuildParagraph(string text, ThemeBrushes b)
    {
        var para = new Paragraph { Margin = new Thickness(0, 4, 0, 6) };
        para.Inlines.AddRange(ParseInlines(text, b));
        return para;
    }

    // ── Bullet list ─────────────────────────────────────────────────

    private static List BuildList(ListBlock l, ThemeBrushes b)
    {
        var list = new List
        {
            MarkerStyle = l.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin      = new Thickness(0, 2, 0, 6),
            // Numbered lists need more left padding because the decimal
            // marker renders wider than a bullet glyph - 28px keeps
            // two-digit numbers aligned with the surrounding text.
            Padding     = new Thickness(l.Ordered ? 28 : 22, 0, 0, 0),
        };
        foreach (var item in l.Items)
        {
            var li = new ListItem { Margin = new Thickness(0, 1, 0, 1) };
            li.Blocks.Add(BuildParagraph(item, b));
            list.ListItems.Add(li);
        }
        return list;
    }

    // ── Blockquote ──────────────────────────────────────────────────

    private static Section BuildQuote(QuoteBlock q, ThemeBrushes b)
    {
        var section = new Section
        {
            BorderBrush     = b.Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding         = new Thickness(14, 4, 0, 4),
            Margin          = new Thickness(0, 6, 0, 6),
            Foreground      = b.Secondary,
        };
        section.Blocks.Add(BuildParagraph(q.Text, b));
        return section;
    }

    // ── Horizontal rule ─────────────────────────────────────────────

    private static Paragraph BuildHorizontalRule(ThemeBrushes b)
    {
        return new Paragraph
        {
            BorderBrush     = b.Accent,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Margin          = new Thickness(0, 12, 0, 12),
        };
    }

    // ── Table ───────────────────────────────────────────────────────

    private static Table BuildTable(TableBlock t, ThemeBrushes b)
    {
        if (t.Rows.Count == 0) return new Table();

        int colCount = t.Rows.Max(r => r.Length);

        var table = new Table
        {
            CellSpacing     = 0,
            BorderBrush     = b.Border,
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin          = new Thickness(0, 8, 0, 12),
        };

        for (int c = 0; c < colCount; c++)
        {
            table.Columns.Add(new TableColumn
            {
                Width = c == colCount - 1
                    ? new GridLength(2, GridUnitType.Star)
                    : new GridLength(1, GridUnitType.Star),
            });
        }

        var rg = new TableRowGroup();
        table.RowGroups.Add(rg);

        for (int r = 0; r < t.Rows.Count; r++)
        {
            var row = new TableRow();
            bool isHeader = r == 0;

            for (int c = 0; c < colCount; c++)
            {
                string cellText = c < t.Rows[r].Length ? t.Rows[r][c] : "";
                var cell = new TableCell
                {
                    Padding         = new Thickness(10, 5, 10, 5),
                    BorderBrush     = b.Border,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                };

                if (isHeader)
                {
                    cell.Background = b.TableHeaderBg;
                    cell.FontWeight = FontWeights.SemiBold;
                }
                else if (r % 2 == 0)
                {
                    cell.Background = b.RowAlt;
                }

                cell.Blocks.Add(BuildParagraph(cellText, b));
                row.Cells.Add(cell);
            }
            rg.Rows.Add(row);
        }

        return table;
    }

    // ── Code block with syntax highlighting + copy button ───────────

    private static BlockUIContainer BuildCodeBlock(CodeBlock c, ThemeBrushes b)
    {
        var rawText = c.Code.TrimEnd('\n', ' ', '\t');
        var lang = c.Language.ToLowerInvariant();

        var codeDisplay = new TextBlock
        {
            FontFamily     = new System.Windows.Media.FontFamily("Consolas, Cascadia Code, Courier New"),
            FontSize       = 12.5,
            Foreground     = b.Primary,
            TextWrapping   = TextWrapping.NoWrap,
            Padding        = new Thickness(14, 10, 40, 10),
        };
        foreach (var inline in SyntaxHighlight(rawText, lang, b))
            codeDisplay.Inlines.Add(inline);

        var scroll = new ScrollViewer
        {
            Content = codeDisplay,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            Background      = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
        };

        var langLabel = new TextBlock
        {
            Text       = string.IsNullOrEmpty(lang) ? "" : lang,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize   = 10,
            Foreground = b.Secondary,
            Padding    = new Thickness(0, 6, 36, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment   = System.Windows.VerticalAlignment.Top,
        };

        var copyGlyph = new TextBlock
        {
            Text       = "\uE8C8",
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize   = 12,
            Foreground = b.Secondary,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = System.Windows.VerticalAlignment.Center,
        };
        var copyBtn = new Button
        {
            Content = copyGlyph,
            Width   = 26, Height = 22,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment   = System.Windows.VerticalAlignment.Top,
            Margin          = new Thickness(0, 4, 4, 0),
            Background      = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
            ToolTip         = "Copy code",
            Cursor          = Cursors.Hand,
            FocusVisualStyle = null,
        };
        copyBtn.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(rawText);
                copyGlyph.Text = "\uE73E";
                copyGlyph.Foreground = b.Accent;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
                timer.Tick += (_, _) =>
                {
                    copyGlyph.Text = "\uE8C8";
                    copyGlyph.Foreground = b.Secondary;
                    timer.Stop();
                };
                timer.Start();
            }
            // must-stay-silent: clipboard write may fail if another app holds
            // the clipboard. The visual fallback (icon stays the original) is
            // the documented user feedback; logging would noise on every retry.
            catch { }
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(langLabel, 0);
        Grid.SetRow(copyBtn, 0);
        Grid.SetRow(scroll, 1);
        Grid.SetColumnSpan(scroll, 1);

        grid.Children.Add(langLabel);
        grid.Children.Add(copyBtn);
        grid.Children.Add(scroll);

        var border = new Border
        {
            Background      = b.InputBg,
            BorderBrush     = b.Border,
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius    = new CornerRadius(6),
            Margin          = new Thickness(0, 6, 0, 10),
            Child           = grid,
            SnapsToDevicePixels = true,
        };
        return new BlockUIContainer(border);
    }
}
