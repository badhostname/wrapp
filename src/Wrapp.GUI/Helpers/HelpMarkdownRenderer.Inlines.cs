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
    // Inline rendering -- syntax highlighting (PowerShell / JSON / INI),
    // span-level emphasis / code / links, plus icon and badge inlines.
    // -----------------------------------------------------------------------

    private static List<Inline> SyntaxHighlight(string code, string language, ThemeBrushes b)
    {
        return language switch
        {
            "powershell" or "ps1" => HighlightPowerShell(code, b),
            "json"                => HighlightJson(code, b),
            "ini"                 => HighlightIni(code, b),
            _                     => new List<Inline> { new Run(code) { Foreground = b.Primary } },
        };
    }

    // ── PowerShell highlighter ──────────────────────────────────────

    // internal so tests can verify the regex consumes every input character
    // (regression guard for the digit-drop bug where embedded numerics were
    // silently skipped because of a word-boundary mismatch).
    internal static readonly Regex PsTokenPattern = new(
        @"(?<comment>#.*)$" +
        @"|(?<string>""[^""]*""|'[^']*')" +
        @"|(?<variable>\$[\w]+)" +
        @"|(?<cmdlet>[A-Za-z][\w]*-[A-Za-z][\w]*(?:\.[\w]+)?)" +
        @"|(?<param>-[A-Za-z][\w]*)" +
        @"|(?<number>\b\d+\.?\d*\b)" +
        @"|(?<keyword>\b(?:if|else|elseif|foreach|for|while|do|switch|function|param|return|try|catch|finally|throw|begin|end|process|break|continue|exit|in)\b)" +
        // text allows digits so embedded numerics inside identifiers/paths
        // (e.g. `v1.0` in `WindowsPowerShell\v1.0\powershell.exe`) are
        // consumed wholesale. Without this, `1` has no word boundary on
        // its left (preceded by `v`), so `number` doesn't match, AND text
        // can't match because digits were excluded -- the regex engine
        // silently skips the digit, dropping it from the rendered output.
        @"|(?<text>[^#""\$\-\s]+)" +
        @"|(?<ws>\s+)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static List<Inline> HighlightPowerShell(string code, ThemeBrushes b)
    {
        var inlines = new List<Inline>();

        foreach (Match m in PsTokenPattern.Matches(code))
        {
            Brush fg;
            if (m.Groups["comment"].Success)        fg = b.Secondary;
            else if (m.Groups["string"].Success)    fg = b.String;
            else if (m.Groups["variable"].Success)  fg = b.Accent;
            else if (m.Groups["cmdlet"].Success)    fg = b.CmdletHighlight;
            else if (m.Groups["param"].Success)     fg = b.Accent;
            else if (m.Groups["number"].Success)    fg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD4, 0xA8, 0x40));
            else if (m.Groups["keyword"].Success)   fg = b.Keyword;
            else                                     fg = b.Primary;

            inlines.Add(new Run(m.Value) { Foreground = fg });
        }

        if (inlines.Count == 0)
            inlines.Add(new Run(code) { Foreground = b.Primary });

        return inlines;
    }

    // ── JSON highlighter ────────────────────────────────────────────

    private static readonly Regex JsonTokenPattern = new(
        @"(?<key>""[^""]*"")\s*:" +
        @"|(?<string>""[^""]*"")" +
        @"|(?<bool>\b(?:true|false)\b)" +
        @"|(?<null>\bnull\b)" +
        @"|(?<number>-?\d+\.?\d*(?:[eE][+-]?\d+)?)" +
        @"|(?<punct>[{}\[\]:,])" +
        @"|(?<ws>\s+)" +
        @"|(?<text>[^\s""{}[\]:,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<Inline> HighlightJson(string code, ThemeBrushes b)
    {
        var inlines = new List<Inline>();

        foreach (Match m in JsonTokenPattern.Matches(code))
        {
            Brush fg;
            if (m.Groups["key"].Success)          fg = b.Keyword;
            else if (m.Groups["string"].Success)  fg = b.String;
            else if (m.Groups["bool"].Success)    fg = b.Accent;
            else if (m.Groups["null"].Success)    fg = b.Accent;
            else if (m.Groups["number"].Success)  fg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD4, 0xA8, 0x40));
            else if (m.Groups["punct"].Success)   fg = b.Secondary;
            else                                   fg = b.Primary;

            // For key matches, the colon is not part of the group — emit
            // the key coloured and let the next token pick up the colon.
            inlines.Add(new Run(m.Value) { Foreground = fg });
        }

        if (inlines.Count == 0)
            inlines.Add(new Run(code) { Foreground = b.Primary });

        return inlines;
    }

    // ── INI highlighter ─────────────────────────────────────────────

    private static List<Inline> HighlightIni(string code, ThemeBrushes b)
    {
        var inlines = new List<Inline>();

        foreach (var rawLine in code.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (inlines.Count > 0) inlines.Add(new Run("\n") { Foreground = b.Primary });

            var trimmed = line.TrimStart();

            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                inlines.Add(new Run(line) { Foreground = b.Secondary });
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.Contains(']'))
            {
                inlines.Add(new Run(line) { Foreground = b.Keyword });
                continue;
            }

            var eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                inlines.Add(new Run(line[..(eqIdx + 1)]) { Foreground = b.Accent });
                inlines.Add(new Run(line[(eqIdx + 1)..])  { Foreground = b.Primary });
                continue;
            }

            inlines.Add(new Run(line) { Foreground = b.Primary });
        }

        if (inlines.Count == 0)
            inlines.Add(new Run(code) { Foreground = b.Primary });

        return inlines;
    }

    // ── Inline parser ───────────────────────────────────────────────

    private static readonly Regex InlinePattern = new(
        @"\[icon:([^\]]+)\]" +                      // [icon:name]
        @"|\[btn:([^\]]+)\]" +                      // [btn:Text]
        @"|\[badge:([^\]:]+):([^\]]+)\]" +          // [badge:Text:color]
        @"|\*\*(.+?)\*\*" +                         // bold
        @"|\*([^\*\n]+?)\*" +                       // italic
        @"|`([^`\n]+?)`" +                          // inline code
        @"|\[([^\]]+)\]\(([^)]+)\)" +               // link
        @"|(&amp;|&lt;|&gt;|&#x[\dA-Fa-f]+;|&#\d+;|&\w+;)" + // HTML entities
        @"|([^*`\[&]+)" +                           // plain text
        @"|(.)",                                    // fallback single char
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static List<Inline> ParseInlines(string text, ThemeBrushes b)
    {
        var inlines = new List<Inline>();

        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Groups[1].Success) // [icon:name]
            {
                inlines.Add(BuildIconInline(m.Groups[1].Value.Trim(), b));
            }
            else if (m.Groups[2].Success) // [btn:Text]
            {
                inlines.Add(BuildButtonBadge(m.Groups[2].Value.Trim(), b));
            }
            else if (m.Groups[3].Success) // [badge:Text:color]
            {
                inlines.Add(BuildStatusBadge(m.Groups[3].Value.Trim(), m.Groups[4].Value.Trim()));
            }
            else if (m.Groups[5].Success) // bold
            {
                var bold = new Bold();
                bold.Inlines.AddRange(ParseInlines(m.Groups[5].Value, b));
                inlines.Add(bold);
            }
            else if (m.Groups[6].Success) // italic
            {
                var italic = new Italic();
                italic.Inlines.AddRange(ParseInlines(m.Groups[6].Value, b));
                inlines.Add(italic);
            }
            else if (m.Groups[7].Success) // inline code
            {
                var codeBorder = new Border
                {
                    Background      = b.InputBg,
                    CornerRadius    = new CornerRadius(3),
                    Padding         = new Thickness(4, 1, 4, 1),
                    Margin          = new Thickness(1, 0, 1, 0),
                    Child = new TextBlock
                    {
                        Text       = m.Groups[7].Value,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Code, Courier New"),
                        FontSize   = 12,
                        Foreground = b.Primary,
                    },
                };
                inlines.Add(new InlineUIContainer(codeBorder) { BaselineAlignment = BaselineAlignment.TextBottom });
            }
            else if (m.Groups[8].Success) // link
            {
                var hyperlink = new Hyperlink(new Run(m.Groups[8].Value))
                {
                    NavigateUri     = new Uri(m.Groups[9].Value, UriKind.RelativeOrAbsolute),
                    Foreground      = b.AccentBg,
                    TextDecorations = TextDecorations.Underline,
                };
                hyperlink.RequestNavigate += (_, e) =>
                {
                    try
                    {
                        // SEC-5: shell-execute launches ANY registered scheme
                        // (file:, ms-*:, \\unc). Help/changelog links are web
                        // links — allow only http(s).
                        if (e.Uri.IsAbsoluteUri
                            && e.Uri.Scheme != Uri.UriSchemeHttp
                            && e.Uri.Scheme != Uri.UriSchemeHttps)
                        {
                            e.Handled = true;
                            return;
                        }
                        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                        {
                            UseShellExecute = true,
                        });
                    }
                    // must-stay-silent: external link click can fail if no
                    // browser is registered for the scheme, or if a security
                    // policy blocks Process.Start. Doing nothing keeps the
                    // help popup intact; logging would surface noise that
                    // doesn't help the user.
                    catch { }
                    e.Handled = true;
                };
                inlines.Add(hyperlink);
            }
            else if (m.Groups[10].Success) // HTML entity
            {
                inlines.Add(new Run(DecodeHtmlEntity(m.Groups[10].Value)));
            }
            else if (m.Groups[11].Success) // plain text
            {
                inlines.Add(new Run(m.Groups[11].Value));
            }
            else if (m.Groups[12].Success) // fallback
            {
                inlines.Add(new Run(m.Groups[12].Value));
            }
        }

        return inlines;
    }

    // ── Icon inline ───────────────────────────────────────────────

    private static Inline BuildIconInline(string name, ThemeBrushes b)
    {
        if (IconMap.TryGetValue(name, out var symbol))
        {
            // wpf-ui's SymbolIcon Foreground propagation to its inner
            // TextBlock breaks when the icon is hosted inside an
            // InlineUIContainer → FlowDocument. Sidestep it: render the
            // glyph via a plain TextBlock using the Fluent font shipped
            // inside the Wpf.Ui assembly. The pack URI is stable even if
            // the resource-key name changes between wpf-ui versions, and
            // SymbolRegular enum values ARE the Unicode codepoints.
            var glyph = new TextBlock
            {
                Text       = ((char)symbol).ToString(),
                FontFamily = FluentIconFont,
                FontSize   = 22,
                TextAlignment     = System.Windows.TextAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            glyph.SetResourceReference(
                TextBlock.ForegroundProperty,
                "TextMutedBrush");
            return new InlineUIContainer(glyph)
            {
                BaselineAlignment = BaselineAlignment.Center,
            };
        }
        return new Run($"[{name}]") { Foreground = b.Secondary };
    }

    // ── Button badge ────────────────────────────────────────────────

    private static Inline BuildButtonBadge(string label, ThemeBrushes b)
    {
        var stack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

        if (ButtonGlyphs.TryGetValue(label, out var glyph))
        {
            stack.Children.Add(new TextBlock
            {
                Text       = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize   = 11,
                Foreground = b.Primary,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin     = new Thickness(0, 0, 5, 0),
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text       = label,
            FontSize   = 12,
            Foreground = b.Primary,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
        });

        var border = new Border
        {
            Background      = b.ButtonBg,
            BorderBrush     = b.Border,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(7, 3, 7, 3),
            Margin          = new Thickness(2, 0, 2, 0),
            Child           = stack,
        };

        return new InlineUIContainer(border)
        {
            BaselineAlignment = BaselineAlignment.Center,
        };
    }

    // ── Status badge ──────────────────────────────────────────────

    private static Inline BuildStatusBadge(string label, string colorKey)
    {
        Brush bg;
        if (BadgeColors.TryGetValue(colorKey, out var hex))
            bg = (Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
        else if (colorKey.StartsWith('#'))
            bg = (Brush)new System.Windows.Media.BrushConverter().ConvertFromString(colorKey)!;
        else
            bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x61, 0x61, 0x61));

        var border = new Border
        {
            Background      = bg,
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(6, 1, 6, 1),
            Margin          = new Thickness(2, 0, 2, 0),
            Child = new TextBlock
            {
                Text       = label,
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White,
            },
        };

        return new InlineUIContainer(border)
        {
            BaselineAlignment = BaselineAlignment.Center,
        };
    }

    private static string DecodeHtmlEntity(string entity) => entity switch
    {
        "&amp;"  => "&",
        "&lt;"   => "<",
        "&gt;"   => ">",
        _ => System.Net.WebUtility.HtmlDecode(entity) ?? entity,
    };
}
