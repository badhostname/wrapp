using System.Text.Json.Nodes;

namespace Wrapp.Helpers;

/// <summary>
/// Type-coerced primitive readers over <see cref="JsonObject"/> with a
/// "return the default value on type mismatch" contract. Eliminates the
/// repeated <c>o[key]?.GetValue&lt;T&gt;() ?? default</c> boilerplate that
/// existed in <see cref="Wrapp.Services.ConfigFileService"/> and is
/// reusable by any other service that parses arbitrary <c>JsonObject</c>
/// trees (e.g. future REST-response shape adapters, defaults loaders, etc).
/// </summary>
/// <remarks>
/// <para>
/// All methods are <c>must-stay-silent on type mismatch</c> by design:
/// returning the documented default value is the explicit fallback path,
/// and logging would create noise on every legitimate type mismatch
/// (e.g. a JSON property typed as the string <c>"false"</c> read as a
/// <see langword="bool"/>).
/// </para>
/// <para>
/// Use:
/// <code>
/// var name    = obj.Str("Name");
/// var enabled = obj.Bool("Enabled", defaultValue: true);
/// var minutes = obj.Int("Minutes", defaultValue: 60);
/// var tags    = obj.StrArray("Tags");
/// var mode    = obj.EnumOr&lt;UpdateMode&gt;("UpdateMode", UpdateMode.Create);
/// </code>
/// </para>
/// </remarks>
public static class JsonObjectExtensions
{
    /// <summary>
    /// Reads a string-typed property. Returns <paramref name="defaultValue"/>
    /// when the key is missing, the value is null, or the value isn't a
    /// string token.
    /// </summary>
    public static string Str(this JsonObject o, string key, string defaultValue = "")
        => o[key]?.GetValue<string>() ?? defaultValue;

    /// <summary>
    /// Reads a bool-typed property with type coercion. Returns
    /// <paramref name="defaultValue"/> when the key is missing or the value
    /// isn't a bool token (string-typed <c>"true"</c>/<c>"false"</c> are NOT
    /// coerced -- callers needing that behaviour should preprocess upstream).
    /// </summary>
    public static bool Bool(this JsonObject o, string key, bool defaultValue = false)
    {
        if (o[key] is JsonValue v)
        {
            try { return v.GetValue<bool>(); } catch { }
        }
        return defaultValue;
    }

    /// <summary>
    /// Reads an int-typed property. Returns <paramref name="defaultValue"/>
    /// when the key is missing or the value isn't an int token.
    /// </summary>
    public static int Int(this JsonObject o, string key, int defaultValue = 0)
    {
        if (o[key] is JsonValue v)
        {
            try { return v.GetValue<int>(); } catch { }
        }
        return defaultValue;
    }

    /// <summary>
    /// Reads a JSON array property as a stream of non-empty strings. Missing
    /// keys, non-array values, and null/empty array elements all yield no
    /// output (never throws on shape mismatch).
    /// </summary>
    public static IEnumerable<string> StrArray(this JsonObject o, string key)
    {
        if (o[key] is not JsonArray arr) return Enumerable.Empty<string>();
        return arr.Select(n => n?.GetValue<string>() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s));
    }

    /// <summary>
    /// Parses a string-typed JSON property into a strongly-typed enum.
    /// Case-insensitive so legacy / hand-edited Config.json with drift in
    /// casing (<c>"create"</c> vs <c>"Create"</c>) still loads. Falls back
    /// to <paramref name="defaultValue"/> when the property is missing OR
    /// the string doesn't match any enum member -- returning the default
    /// is strictly better than throwing mid-parse for a value we can recover.
    /// </summary>
    public static T EnumOr<T>(this JsonObject o, string key, T defaultValue) where T : struct, Enum
    {
        var s = o.Str(key);
        if (string.IsNullOrEmpty(s)) return defaultValue;
        return Enum.TryParse<T>(s, ignoreCase: true, out var v) ? v : defaultValue;
    }
}
