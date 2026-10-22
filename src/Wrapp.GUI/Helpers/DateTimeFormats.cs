using System.Globalization;

namespace Wrapp.Helpers;

/// <summary>
/// Centralises ISO-8601 / SCCM / Intune date-time format strings used across
/// the GUI. Previously these literals were repeated in
/// <c>DateTimePickerField</c>, <c>SCCMViewModel</c>, <c>TemplateService</c>,
/// <c>BundleService</c>, and <c>GeneralViewModel</c>; the 0227 MinValue bug
/// surfaced partly because the SCCM cmdlet and the GUI weren&#x2019;t speaking the
/// same format consistently.
///
/// Constants here are the canonical surface for date rendering / parsing -
/// a planned lint rule will reject inline <c>"yyyy-MM-dd..."</c> literals
/// outside this file.
/// </summary>
public static class DateTimeFormats
{
    /// <summary>
    /// SCCM &amp; Intune wire format: ISO-8601 UTC with millisecond precision,
    /// trailing <c>Z</c>. Matches the strings written by the SCCM/Intune APIs
    /// (e.g. <c>2026-06-03T14:30:00.000Z</c>). Always with <c>.000</c>, not the
    /// dynamic <c>fff</c> - downstream cmdlets are picky about the trailing
    /// zeros and treat the absence of <c>.fff</c> as a parse error.
    /// </summary>
    public const string IsoUtc = "yyyy-MM-ddTHH:mm:ss.000Z";

    /// <summary>ISO date with no time component (e.g. <c>2026-06-03</c>).</summary>
    public const string IsoDateOnly = "yyyy-MM-dd";

    /// <summary>ISO date + minute precision (e.g. <c>2026-06-03 14:30</c>). Display only.</summary>
    public const string IsoDateMinute = "yyyy-MM-dd HH:mm";

    /// <summary>ISO date + second precision (e.g. <c>2026-06-03 14:30:00</c>). Display only.</summary>
    public const string IsoDateSeconds = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Tolerant input formats accepted when parsing user-supplied or
    /// upstream-supplied date strings (<see cref="TryParseFlexible"/>).
    /// First-match wins; canonical <see cref="IsoUtc"/> comes first because
    /// every Wrapp-written value uses it.
    /// </summary>
    public static readonly string[] FlexibleInputFormats =
    {
        IsoUtc,
        "yyyy-MM-ddTHH:mm:ss.fffZ",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss",
        IsoDateSeconds,
        IsoDateOnly,
    };

    /// <summary>Formats <paramref name="dt"/> as an ISO-8601 UTC string using <see cref="IsoUtc"/>.</summary>
    public static string ToIsoUtc(this DateTime dt)
        => dt.ToUniversalTime().ToString(IsoUtc, CultureInfo.InvariantCulture);

    /// <summary>Formats <paramref name="dt"/> as <see cref="IsoDateOnly"/>.</summary>
    public static string ToIsoDateOnly(this DateTime dt)
        => dt.ToString(IsoDateOnly, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an ISO-8601 UTC string in the strict <see cref="IsoUtc"/> form.
    /// </summary>
    public static bool TryParseIsoUtc(string s, out DateTime dt)
        => DateTime.TryParseExact(
            s?.Trim() ?? string.Empty,
            IsoUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out dt);

    /// <summary>
    /// Parses a date-time string against the <see cref="FlexibleInputFormats"/>
    /// set. Returns <c>true</c> on the first matching format. Result is in UTC.
    /// </summary>
    public static bool TryParseFlexible(string s, out DateTime dt)
        => DateTime.TryParseExact(
            s?.Trim() ?? string.Empty,
            FlexibleInputFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out dt);
}
