using System.Globalization;
using Wrapp.Models;

namespace Wrapp.Helpers;

/// <summary>
/// Per-<see cref="FieldKind"/> value validation. Each validator returns a
/// human-readable error string when the value is invalid, or <c>null</c> when
/// the value is acceptable. Empty / null values are treated as acceptable here;
/// the "required" check is handled separately by <see cref="FieldStateProvider"/>.
/// </summary>
public static class FieldValidators
{
    public static string? Validate(FieldDescriptor descriptor, object? rawValue)
    {
        var s = (rawValue?.ToString() ?? string.Empty).Trim();

        if (s.Length == 0)
            return descriptor.Required ? "Required" : null;

        return descriptor.Kind switch
        {
            FieldKind.String   => null,
            FieldKind.Int      => ValidateInt(s, descriptor.Min, descriptor.Max),
            FieldKind.Bool     => ValidateBool(s),
            FieldKind.Guid     => ValidateGuid(s),
            FieldKind.IsoDate  => ValidateIsoDate(s),
            FieldKind.Url      => ValidateUrl(s),
            FieldKind.ComboBox => ValidateComboBox(s, descriptor.AllowedValues),
            _ => null,
        };
    }

    private static string? ValidateInt(string s, int? min, int? max)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return "Must be a whole number";
        if (min is not null && n < min) return $"Must be at least {min}";
        if (max is not null && n > max) return $"Must be at most {max}";
        return null;
    }

    private static string? ValidateBool(string s)
        => bool.TryParse(s, out _) ? null : "Must be true or false";

    private static string? ValidateGuid(string s)
        => System.Guid.TryParse(s, out _) ? null : "Must be a valid GUID";

    private static string? ValidateIsoDate(string s)
        => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            ? null
            : "Must be a valid ISO-8601 date/time";

    private static string? ValidateUrl(string s)
        => IsHttpUrlInvalid(s) ? "Must be a valid http(s) URL" : null;

    /// <summary>
    /// Single source of truth for http(s)-URL validity. Returns <c>true</c>
    /// when <paramref name="value"/> is non-empty but is NOT a valid absolute
    /// http/https URL. Empty / whitespace is treated as valid (not an error)
    /// — the "required" check is handled separately. Used by both
    /// <see cref="ValidateUrl"/> and the package models' URL indicators.
    /// </summary>
    public static bool IsHttpUrlInvalid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return !(Uri.TryCreate(value, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
    }

    private static string? ValidateComboBox(string s, string[]? allowed)
    {
        if (allowed is null || allowed.Length == 0) return null;
        return allowed.Any(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"Must be one of: {string.Join(", ", allowed)}";
    }
}
