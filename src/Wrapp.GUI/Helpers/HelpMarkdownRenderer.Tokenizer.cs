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
    // Markdown tokenizer -- raw text -> List<MdBlock>. The MdBlock records
    // themselves stay in the root file alongside the public Render entry
    // point so a contributor opening that file sees the data shape upfront.
    // -----------------------------------------------------------------------

    private static readonly Regex OrderedListPattern = new(
        @"^\d+\.\s+(.*)$",
        RegexOptions.Compiled);

    // ── Block tokenizer ─────────────────────────────────────────────

    // internal for test access (see InternalsVisibleTo in Wrapp.GUI.csproj)
    internal static List<MdBlock> Tokenize(string text)
    {
        var blocks = new List<MdBlock>();
        var lines = text.Split('\n');

        bool inCode = false;
        string codeLang = "";
        var codeLines = new StringBuilder();

        var tableRows = new List<string[]>();
        var listItems = new List<string>();
        bool listOrdered = false;
        var paraLines = new StringBuilder();
        var quoteLines = new StringBuilder();

        void FlushParagraph()
        {
            if (paraLines.Length > 0)
            {
                blocks.Add(new ParagraphBlock(paraLines.ToString().Trim()));
                paraLines.Clear();
            }
        }
        void FlushList()
        {
            if (listItems.Count > 0)
            {
                blocks.Add(new ListBlock(new List<string>(listItems), listOrdered));
                listItems.Clear();
                listOrdered = false;
            }
        }
        void FlushTable()
        {
            if (tableRows.Count > 0)
            {
                blocks.Add(new TableBlock(new List<string[]>(tableRows)));
                tableRows.Clear();
            }
        }
        void FlushQuote()
        {
            if (quoteLines.Length > 0)
            {
                blocks.Add(new QuoteBlock(quoteLines.ToString().Trim()));
                quoteLines.Clear();
            }
        }
        void FlushAll() { FlushParagraph(); FlushList(); FlushTable(); FlushQuote(); }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');

            // Inside a fenced code block - accumulate until closing fence.
            if (inCode)
            {
                if (raw.TrimStart().StartsWith("```"))
                {
                    inCode = false;
                    blocks.Add(new CodeBlock(codeLang, codeLines.ToString()));
                    codeLines.Clear();
                }
                else
                {
                    if (codeLines.Length > 0) codeLines.Append('\n');
                    codeLines.Append(raw);
                }
                continue;
            }

            var trimmed = raw.TrimStart();

            // Opening fence
            if (trimmed.StartsWith("```"))
            {
                FlushAll();
                inCode = true;
                codeLang = trimmed.Length > 3 ? trimmed[3..].Trim() : "";
                continue;
            }

            // Heading (H1 = hero, H2 = section, H3 = sub-section)
            if (trimmed.StartsWith("# ") || trimmed.StartsWith("## ") || trimmed.StartsWith("### "))
            {
                FlushAll();
                int level = trimmed.StartsWith("### ") ? 3 : trimmed.StartsWith("## ") ? 2 : 1;
                blocks.Add(new HeadingBlock(level, trimmed[(level + 1)..].Trim()));
                continue;
            }

            // Horizontal rule
            if (trimmed == "---" || trimmed == "***" || trimmed == "___")
            {
                FlushAll();
                blocks.Add(new HorizontalRuleBlock());
                continue;
            }

            // Table row
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph(); FlushList(); FlushQuote();
                // Skip separator rows like |---|---|
                if (Regex.IsMatch(trimmed, @"^\|[\s\-:|\+]+\|?\s*$")) continue;
                var cells = ParseTableRow(trimmed);
                tableRows.Add(cells);
                continue;
            }
            else if (tableRows.Count > 0)
            {
                FlushTable();
            }

            // Bullet list item (`- text`)
            if (trimmed.StartsWith("- "))
            {
                FlushParagraph(); FlushTable(); FlushQuote();
                // Flush a previously-open numbered list before starting a bullet one.
                if (listOrdered && listItems.Count > 0) FlushList();
                listOrdered = false;
                listItems.Add(trimmed[2..].Trim());
                continue;
            }

            // Numbered list item (`1. text`, `42. text`, etc.)
            var orderedMatch = OrderedListPattern.Match(trimmed);
            if (orderedMatch.Success)
            {
                FlushParagraph(); FlushTable(); FlushQuote();
                if (!listOrdered && listItems.Count > 0) FlushList();
                listOrdered = true;
                listItems.Add(orderedMatch.Groups[1].Value.Trim());
                continue;
            }

            if (listItems.Count > 0 && !string.IsNullOrWhiteSpace(trimmed))
            {
                // Continuation of previous list item (indented line)
                if (raw.StartsWith("  ") || raw.StartsWith("\t"))
                {
                    listItems[^1] += " " + trimmed;
                    continue;
                }
                FlushList();
            }
            else if (listItems.Count > 0 && string.IsNullOrWhiteSpace(trimmed))
            {
                FlushList();
            }

            // Blockquote
            if (trimmed.StartsWith("> ") || trimmed == ">")
            {
                FlushParagraph(); FlushList(); FlushTable();
                var content = trimmed.Length > 2 ? trimmed[2..] : "";
                if (quoteLines.Length > 0) quoteLines.Append('\n');
                quoteLines.Append(content);
                continue;
            }
            else if (quoteLines.Length > 0)
            {
                FlushQuote();
            }

            // Blank line
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushAll();
                continue;
            }

            // Paragraph text - accumulate
            if (paraLines.Length > 0) paraLines.Append(' ');
            paraLines.Append(trimmed);
        }

        // Flush any remaining open block
        if (inCode)
            blocks.Add(new CodeBlock(codeLang, codeLines.ToString()));
        FlushAll();

        return blocks;
    }

    private static string[] ParseTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|', StringSplitOptions.None)
                      .Select(c => c.Trim())
                      .ToArray();
    }

    internal static string PreprocessLegacyFieldLines(string raw)
    {
        var output = new StringBuilder(raw.Length + 64);
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart();

            if (string.IsNullOrWhiteSpace(line)
                || line.StartsWith("#")
                || line.StartsWith("-")
                || line.StartsWith("*")
                || line.StartsWith(">")
                || line.StartsWith("|")
                || line.StartsWith("```")
                || line.StartsWith("`")
                || line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || OrderedListPattern.IsMatch(line))
            {
                output.AppendLine(line);
                continue;
            }

            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0 && colonIdx < 40)
            {
                var label = line[..colonIdx].Trim();
                var body = line[(colonIdx + 1)..].Trim();

                if (!label.Contains('*') && !label.Contains('`')
                    && (!label.Contains(' ') || label.Length <= 30))
                {
                    output.AppendLine($"**{label}:** {body}");
                    continue;
                }
            }

            output.AppendLine(line);
        }
        return output.ToString();
    }
}
