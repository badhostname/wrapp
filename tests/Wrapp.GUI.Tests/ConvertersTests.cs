using System.Globalization;
using System.Windows;
using Wrapp;

namespace Wrapp.Tests;

/// <summary>
/// Pure-logic tests for the IValueConverter implementations in
/// Helpers/Converters.cs. Two resource-lookup converters
/// (ConnectionStateToBrush, PackageOutcomeToBrush) are omitted - they
/// depend on <c>Application.Current</c> which isn't bootstrapped in
/// the test host.
/// </summary>
public class ConvertersTests
{
    private static readonly CultureInfo Cult = CultureInfo.InvariantCulture;

    // ── IntToVisibilityConverter ────────────────────────────────────

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IntToVisibility_Convert(int input, bool expectVisible)
    {
        var c = new IntToVisibilityConverter();
        var expected = expectVisible ? Visibility.Visible : Visibility.Collapsed;
        Assert.Equal(expected, c.Convert(input, typeof(Visibility), null!, Cult));
    }

    [Fact]
    public void IntToVisibility_NonIntValue_CollapsesDefensively()
    {
        var c = new IntToVisibilityConverter();
        // Not an int -> Collapsed rather than throw.
        Assert.Equal(Visibility.Collapsed, c.Convert("5", typeof(Visibility), null!, Cult));
        Assert.Equal(Visibility.Collapsed, c.Convert(null!, typeof(Visibility), null!, Cult));
    }

    [Fact]
    public void IntToVisibility_ConvertBack_Throws()
    {
        var c = new IntToVisibilityConverter();
        Assert.Throws<NotSupportedException>(() =>
            c.ConvertBack(Visibility.Visible, typeof(int), null!, Cult));
    }

    // ── InvertBoolConverter ────────────────────────────────────────

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InvertBool_FlipsBothWays(bool input, bool expected)
    {
        var c = new InvertBoolConverter();
        Assert.Equal(expected, c.Convert(input, typeof(bool), null!, Cult));
        Assert.Equal(expected, c.ConvertBack(input, typeof(bool), null!, Cult));
    }

    [Fact]
    public void InvertBool_NonBoolValue_PassesThrough()
    {
        var c = new InvertBoolConverter();
        Assert.Equal("not-a-bool", c.Convert("not-a-bool", typeof(bool), null!, Cult));
    }

    // ── InverseBoolToVisibilityConverter ───────────────────────────

    [Theory]
    [InlineData(true,  Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    public void InverseBoolToVisibility_Convert(bool input, Visibility expected)
    {
        var c = new InverseBoolToVisibilityConverter();
        Assert.Equal(expected, c.Convert(input, typeof(Visibility), null!, Cult));
    }

    [Fact]
    public void InverseBoolToVisibility_NonBool_FallsToVisible()
    {
        // Treats null / non-bool as "feature is disabled" => show overlay.
        var c = new InverseBoolToVisibilityConverter();
        Assert.Equal(Visibility.Visible, c.Convert(null!,  typeof(Visibility), null!, Cult));
        Assert.Equal(Visibility.Visible, c.Convert("junk", typeof(Visibility), null!, Cult));
    }

    // ── StringMatchToVisibilityConverter ───────────────────────────

    [Theory]
    [InlineData("Intune",      "Intune",         true)]
    [InlineData("INTUNE",      "intune",         true)]   // case-insensitive
    [InlineData("Intune",      "SCCM",           false)]
    [InlineData("embedded",    "embedded|manual", true)]  // pipe-separated OR
    [InlineData("manual",      "embedded|manual", true)]
    [InlineData("csv",         "embedded|manual", false)]
    [InlineData("",            "embedded",       false)]
    public void StringMatchToVisibility_Convert(string value, string param, bool expectVisible)
    {
        var c = new StringMatchToVisibilityConverter();
        var result = c.Convert(value, typeof(Visibility), param, Cult);
        Assert.Equal(expectVisible ? Visibility.Visible : Visibility.Collapsed, result);
    }

    [Fact]
    public void StringMatchToVisibility_NullValue_Collapsed()
    {
        var c = new StringMatchToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed,
            c.Convert(null!, typeof(Visibility), "Intune", Cult));
    }

    // ── EmptyToVisibilityConverter ─────────────────────────────────

    [Theory]
    [InlineData("value", true)]
    [InlineData("",      false)]
    [InlineData("   ",   false)]     // whitespace counts as empty
    [InlineData(null,    false)]
    public void EmptyToVisibility_Convert(string? input, bool expectVisible)
    {
        var c = new EmptyToVisibilityConverter();
        var expected = expectVisible ? Visibility.Visible : Visibility.Collapsed;
        Assert.Equal(expected, c.Convert(input!, typeof(Visibility), null!, Cult));
    }

    // ── EmptyToPlaceholderConverter ────────────────────────────────

    [Theory]
    [InlineData("value",  "value")]
    [InlineData("",       "-")]
    [InlineData("   ",    "-")]
    [InlineData(null,     "-")]
    public void EmptyToPlaceholder_Convert(string? input, string expected)
    {
        var c = new EmptyToPlaceholderConverter();
        Assert.Equal(expected, c.Convert(input!, typeof(string), null!, Cult));
    }

    // ── MarkdownToPlainTextConverter ───────────────────────────────

    [Theory]
    [InlineData("**bold**",           "bold")]
    [InlineData("*italic*",           "italic")]
    [InlineData("`code`",             "code")]
    [InlineData("plain text",         "plain text")]
    [InlineData("**b** and *i*",      "b and i")]
    [InlineData("`cmdlet` is `bold`", "cmdlet is bold")]
    public void MarkdownToPlainText_StripsInlineMarkers(string input, string expected)
    {
        var c = new MarkdownToPlainTextConverter();
        Assert.Equal(expected, c.Convert(input, typeof(string), null!, Cult));
    }

    [Fact]
    public void MarkdownToPlainText_NullInput_ReturnsNull()
    {
        var c = new MarkdownToPlainTextConverter();
        Assert.Null(c.Convert(null!, typeof(string), null!, Cult));
    }

    [Fact]
    public void MarkdownToPlainText_EmptyInput_ReturnsEmpty()
    {
        var c = new MarkdownToPlainTextConverter();
        Assert.Equal("", c.Convert("", typeof(string), null!, Cult));
    }

    [Fact]
    public void MarkdownToPlainText_DoesNotConsumeStandaloneAsterisks()
    {
        // Italic regex requires a closing `*` on the same line -- a lone
        // asterisk (e.g. inside `2 * 3`) must be left alone.
        var c = new MarkdownToPlainTextConverter();
        Assert.Equal("2 * 3 = 6", c.Convert("2 * 3 = 6", typeof(string), null!, Cult));
    }

    [Fact]
    public void MarkdownToPlainText_HandlesBoldInsideItalicCorrectly()
    {
        // Bold is stripped before italic, so **text** doesn't get eaten
        // as two single-asterisk italic runs.
        var c = new MarkdownToPlainTextConverter();
        Assert.Equal("hello", c.Convert("**hello**", typeof(string), null!, Cult));
    }
}
