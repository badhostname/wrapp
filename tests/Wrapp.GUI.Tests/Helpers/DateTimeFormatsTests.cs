using System.Globalization;
using Wrapp.Helpers;

namespace Wrapp.Tests;

/// <summary>
/// Phase 13 (D-4). Covers the canonical date format helpers that replaced
/// the inline <c>"yyyy-MM-dd..."</c> literals scattered through
/// <see cref="Wrapp.Controls.DateTimePickerField"/>,
/// <c>SCCMViewModel</c>, <c>TemplateService</c>, and <c>BundleService</c>.
/// </summary>
public class DateTimeFormatsTests
{
    [Fact]
    public void Constants_MatchExpectedShapes()
    {
        Assert.Equal("yyyy-MM-ddTHH:mm:ss.000Z", DateTimeFormats.IsoUtc);
        Assert.Equal("yyyy-MM-dd",               DateTimeFormats.IsoDateOnly);
        Assert.Equal("yyyy-MM-dd HH:mm",         DateTimeFormats.IsoDateMinute);
        Assert.Equal("yyyy-MM-dd HH:mm:ss",      DateTimeFormats.IsoDateSeconds);
    }

    [Fact]
    public void ToIsoUtc_RoundTripsThroughTryParseIsoUtc()
    {
        // Use millisecond-truncated UTC so the .000Z formatter doesn't drop sub-ms precision.
        var original = new DateTime(2026, 6, 3, 14, 30, 0, DateTimeKind.Utc);
        var formatted = original.ToIsoUtc();

        Assert.Equal("2026-06-03T14:30:00.000Z", formatted);
        Assert.True(DateTimeFormats.TryParseIsoUtc(formatted, out var parsed));
        Assert.Equal(original, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }

    [Fact]
    public void ToIsoUtc_ConvertsLocalKindToUtc()
    {
        // A DateTime with Kind=Local must round-trip via .ToUniversalTime()
        // before formatting; without that, the literal Local hour would
        // appear with a Z suffix and parsers would treat it as UTC, drifting
        // the value by the local offset.
        var local = new DateTime(2026, 6, 3, 14, 30, 0, DateTimeKind.Local);
        var formatted = local.ToIsoUtc();

        Assert.EndsWith("Z", formatted);
        Assert.True(DateTimeFormats.TryParseIsoUtc(formatted, out var parsed));
        Assert.Equal(local.ToUniversalTime(), parsed);
    }

    [Fact]
    public void ToIsoDateOnly_ProducesIsoDateString()
    {
        var dt = new DateTime(2026, 6, 3, 14, 30, 0, DateTimeKind.Utc);
        Assert.Equal("2026-06-03", dt.ToIsoDateOnly());
    }

    [Theory]
    [InlineData("2026-06-03T14:30:00.000Z")]
    [InlineData("2026-06-03T14:30:00.123Z")]
    [InlineData("2026-06-03T14:30:00Z")]
    [InlineData("2026-06-03T14:30:00")]
    [InlineData("2026-06-03 14:30:00")]
    [InlineData("2026-06-03")]
    public void TryParseFlexible_AcceptsAllDocumentedShapes(string input)
    {
        Assert.True(DateTimeFormats.TryParseFlexible(input, out var dt));
        Assert.Equal(2026, dt.Year);
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
    }

    [Fact]
    public void TryParseFlexible_RejectsGarbageInput()
    {
        Assert.False(DateTimeFormats.TryParseFlexible("not a date", out _));
        Assert.False(DateTimeFormats.TryParseFlexible(string.Empty, out _));
    }

    [Fact]
    public void TryParseIsoUtc_RejectsShortenedFormat()
    {
        // The strict IsoUtc parser must not accept date-only -- callers that
        // accept multiple shapes should use TryParseFlexible instead.
        Assert.False(DateTimeFormats.TryParseIsoUtc("2026-06-03", out _));
    }

    [Fact]
    public void IsoUtc_FormatIsInvariantCultureSafe()
    {
        // Force a culture that uses non-Latin digits and separators to prove
        // the InvariantCulture path inside ToIsoUtc rejects locale-specific
        // formatting.
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var dt = new DateTime(2026, 6, 3, 14, 30, 0, DateTimeKind.Utc);
            Assert.Equal("2026-06-03T14:30:00.000Z", dt.ToIsoUtc());
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }
}
