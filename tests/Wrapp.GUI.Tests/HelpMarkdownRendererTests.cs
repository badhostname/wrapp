using Wrapp;

namespace Wrapp.Tests;

/// <summary>
/// Exercises the pure portions of the custom help Markdown renderer:
/// the block tokenizer (no FlowDocument, no theme resolution) and the
/// legacy-field-line preprocessor. These are the two bits of the
/// pipeline most likely to regress since they control how every help
/// string ends up parsed.
/// </summary>
public class HelpMarkdownRendererTests
{
    // ── Headings ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("# Title",         1, "Title")]
    [InlineData("## Sub",          2, "Sub")]
    [InlineData("### Deep",        3, "Deep")]
    [InlineData("##   Extra space", 2, "Extra space")]
    public void Tokenize_HeadingLevelsParseCorrectly(string input, int expectedLevel, string expectedText)
    {
        var blocks = HelpMarkdownRenderer.Tokenize(input);

        var heading = Assert.IsType<HelpMarkdownRenderer.HeadingBlock>(Assert.Single(blocks));
        Assert.Equal(expectedLevel, heading.Level);
        Assert.Equal(expectedText, heading.Text);
    }

    [Fact]
    public void Tokenize_HashWithoutSpace_IsParagraphNotHeading()
    {
        // `#heading` (no space) isn't a valid Markdown heading.
        var blocks = HelpMarkdownRenderer.Tokenize("#notaheading");

        Assert.IsType<HelpMarkdownRenderer.ParagraphBlock>(Assert.Single(blocks));
    }

    // ── Fenced code blocks ───────────────────────────────────────────

    [Fact]
    public void Tokenize_FencedCodeBlock_CapturesLanguageAndContent()
    {
        var md = "```powershell\nGet-Foo\nSet-Bar\n```";

        var blocks = HelpMarkdownRenderer.Tokenize(md);

        var code = Assert.IsType<HelpMarkdownRenderer.CodeBlock>(Assert.Single(blocks));
        Assert.Equal("powershell", code.Language);
        Assert.Equal("Get-Foo\nSet-Bar", code.Code);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\sysnative\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File Script\InstallScript.ps1")]
    [InlineData(@"foo123bar")]            // embedded digits in identifier
    [InlineData(@"version v1.0.0")]        // multiple embedded digits
    [InlineData(@"path\to\file.ps1")]      // trailing digit
    [InlineData(@"Get-Item -Count 60")]    // standalone number still works
    [InlineData(@"$x = 42; Write-Host 1")] // variable + standalone numbers
    public void PsTokenPattern_ConsumesEveryCharacter_NoDigitDrop(string input)
    {
        // Regression: embedded digits inside identifiers used to be
        // silently skipped by the regex engine because the `text` group
        // excluded digits AND the `number` group required word boundaries
        // that don't exist for digits adjacent to letters. Result was
        // `v1.0` rendering as `v.0`. The fix allows digits in `text` so
        // every input character lands in some token's match.
        var totalMatched = HelpMarkdownRenderer.PsTokenPattern
            .Matches(input)
            .Sum(m => m.Length);
        Assert.Equal(input.Length, totalMatched);
    }

    [Fact]
    public void Tokenize_FencedCodeBlock_WithoutLanguage_EmptyLanguageString()
    {
        var blocks = HelpMarkdownRenderer.Tokenize("```\nbare\n```");

        var code = Assert.IsType<HelpMarkdownRenderer.CodeBlock>(Assert.Single(blocks));
        Assert.Equal("", code.Language);
        Assert.Equal("bare", code.Code);
    }

    [Fact]
    public void Tokenize_UnterminatedCodeBlock_StillEmitsCodeBlock()
    {
        // No closing fence -- guard against runaway parsing and make
        // sure we don't just drop the content into a paragraph bucket.
        var blocks = HelpMarkdownRenderer.Tokenize("```\nno-close");

        var code = Assert.IsType<HelpMarkdownRenderer.CodeBlock>(Assert.Single(blocks));
        Assert.Equal("no-close", code.Code);
    }

    [Fact]
    public void Tokenize_CodeBlockContentWithHashChars_IsNotReparsedAsHeadings()
    {
        var md = "```\n# not a heading\n## also not\n```";

        var blocks = HelpMarkdownRenderer.Tokenize(md);

        var code = Assert.IsType<HelpMarkdownRenderer.CodeBlock>(Assert.Single(blocks));
        Assert.Contains("# not a heading", code.Code);
        Assert.Contains("## also not", code.Code);
    }

    // ── Tables ───────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_PipeTable_ParsesRowsAndSkipsSeparator()
    {
        var md = "| A | B |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |";

        var blocks = HelpMarkdownRenderer.Tokenize(md);

        var table = Assert.IsType<HelpMarkdownRenderer.TableBlock>(Assert.Single(blocks));
        // Separator row (|---|---|) should be dropped.
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new[] { "A", "B" }, table.Rows[0]);
        Assert.Equal(new[] { "1", "2" }, table.Rows[1]);
        Assert.Equal(new[] { "3", "4" }, table.Rows[2]);
    }

    [Fact]
    public void Tokenize_TableAlignmentSeparator_IsAlsoSkipped()
    {
        // Markdown allows `:---|:---:|---:` for alignment -- still a separator.
        var md = "| A | B | C |\n|:---|:---:|---:|\n| 1 | 2 | 3 |";

        var blocks = HelpMarkdownRenderer.Tokenize(md);

        var table = Assert.IsType<HelpMarkdownRenderer.TableBlock>(Assert.Single(blocks));
        Assert.Equal(2, table.Rows.Count);
    }

    // ── Lists ────────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_BulletList_EmitsUnorderedListBlock()
    {
        var blocks = HelpMarkdownRenderer.Tokenize("- one\n- two\n- three");

        var list = Assert.IsType<HelpMarkdownRenderer.ListBlock>(Assert.Single(blocks));
        Assert.False(list.Ordered);
        Assert.Equal(new[] { "one", "two", "three" }, list.Items);
    }

    [Fact]
    public void Tokenize_NumberedList_EmitsOrderedListBlock()
    {
        var blocks = HelpMarkdownRenderer.Tokenize("1. first\n2. second\n3. third");

        var list = Assert.IsType<HelpMarkdownRenderer.ListBlock>(Assert.Single(blocks));
        Assert.True(list.Ordered);
        Assert.Equal(new[] { "first", "second", "third" }, list.Items);
    }

    [Fact]
    public void Tokenize_SwitchingListTypes_FlushesFirstListThenStartsSecond()
    {
        // Bullet then numbered -> two separate ListBlocks, not one combined.
        var md = "- bullet\n- items\n1. then\n2. numbered";

        var blocks = HelpMarkdownRenderer.Tokenize(md);

        Assert.Equal(2, blocks.Count);
        Assert.False(((HelpMarkdownRenderer.ListBlock)blocks[0]).Ordered);
        Assert.True(((HelpMarkdownRenderer.ListBlock)blocks[1]).Ordered);
    }

    // ── Blockquotes & HR ─────────────────────────────────────────────

    [Fact]
    public void Tokenize_Blockquote_EmitsQuoteBlock()
    {
        var blocks = HelpMarkdownRenderer.Tokenize("> quoted text");

        var quote = Assert.IsType<HelpMarkdownRenderer.QuoteBlock>(Assert.Single(blocks));
        Assert.Equal("quoted text", quote.Text);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    public void Tokenize_HorizontalRule_AllThreeMarkersRecognised(string marker)
    {
        var blocks = HelpMarkdownRenderer.Tokenize(marker);
        Assert.IsType<HelpMarkdownRenderer.HorizontalRuleBlock>(Assert.Single(blocks));
    }

    // ── Mixed content ───────────────────────────────────────────────

    [Fact]
    public void Tokenize_ParagraphsSeparatedByBlankLines_ProduceDistinctBlocks()
    {
        var md = "first paragraph\n\nsecond paragraph";

        var blocks = HelpMarkdownRenderer.Tokenize(md);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("first paragraph",  ((HelpMarkdownRenderer.ParagraphBlock)blocks[0]).Text);
        Assert.Equal("second paragraph", ((HelpMarkdownRenderer.ParagraphBlock)blocks[1]).Text);
    }

    [Fact]
    public void Tokenize_ConsecutiveNonBlankParagraphLines_MergeIntoOneBlock()
    {
        // No blank line between them -> soft-wrap, single paragraph.
        var md = "line one\nline two";

        var blocks = HelpMarkdownRenderer.Tokenize(md);

        var p = Assert.IsType<HelpMarkdownRenderer.ParagraphBlock>(Assert.Single(blocks));
        Assert.Equal("line one line two", p.Text);
    }

    [Fact]
    public void Tokenize_EmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(HelpMarkdownRenderer.Tokenize(""));
    }

    [Fact]
    public void Tokenize_OnlyWhitespace_ReturnsEmptyList()
    {
        Assert.Empty(HelpMarkdownRenderer.Tokenize("   \n\n\n   "));
    }

    [Fact]
    public void Tokenize_FullMixedDocument_PreservesOrder()
    {
        var md = "# Title\n\nIntro paragraph.\n\n## Sub\n\n- item A\n- item B\n\n```powershell\nGet-Foo\n```";

        var blocks = HelpMarkdownRenderer.Tokenize(md);

        Assert.Collection(blocks,
            b => Assert.Equal(1, Assert.IsType<HelpMarkdownRenderer.HeadingBlock>(b).Level),
            b => Assert.IsType<HelpMarkdownRenderer.ParagraphBlock>(b),
            b => Assert.Equal(2, Assert.IsType<HelpMarkdownRenderer.HeadingBlock>(b).Level),
            b => Assert.IsType<HelpMarkdownRenderer.ListBlock>(b),
            b => Assert.IsType<HelpMarkdownRenderer.CodeBlock>(b));
    }

    // ── PreprocessLegacyFieldLines ─────────────────────────────────

    [Fact]
    public void PreprocessLegacyFieldLines_ShortLabelColon_GetsBolded()
    {
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines("Name: Alice");

        Assert.Contains("**Name:** Alice", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_HeadingLine_IsNotRewritten()
    {
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines("## Not: a field");

        Assert.DoesNotContain("**Not:**", output);
        Assert.Contains("## Not: a field", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_BulletListItem_IsNotRewritten()
    {
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines("- Label: value");

        Assert.DoesNotContain("**Label:**", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_NumberedListItem_IsNotRewritten()
    {
        // Numbered-list guard added in 0.6.0.0169 -- make sure it stays.
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines("1. Step: do thing");

        Assert.DoesNotContain("**1. Step:**", output);
        Assert.Contains("1. Step: do thing", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_CodeBlockFence_IsNotRewritten()
    {
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines("```json\nkey: value\n```");

        // The code-content "key: value" IS rewritten by this preprocessor --
        // but the fences themselves stay intact. That's OK because the
        // tokenizer sees the fence line and treats everything between as
        // opaque code content anyway.
        Assert.Contains("```json", output);
        Assert.Contains("```", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_TableRow_IsNotRewritten()
    {
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines("| Key: value | col |");

        Assert.DoesNotContain("**", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_LabelWithAsterisks_IsNotRewritten()
    {
        // If the label already has Markdown emphasis chars, leave it.
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines("*emphatic*: value");

        Assert.DoesNotContain("****emphatic***:**", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_LabelLongerThan30Chars_IsNotRewritten()
    {
        // The short-label guard prevents rewriting when the label is a
        // multi-word sentence that happens to contain a colon.
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines(
            "this is a very long label that shouldn't be bolded: value");

        Assert.DoesNotContain("**", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_HttpUrl_IsNotRewritten()
    {
        var output = HelpMarkdownRenderer.PreprocessLegacyFieldLines("https://example.com/thing");

        Assert.DoesNotContain("**", output);
        Assert.Contains("https://example.com/thing", output);
    }

    [Fact]
    public void PreprocessLegacyFieldLines_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", HelpMarkdownRenderer.PreprocessLegacyFieldLines("").TrimEnd());
    }
}
