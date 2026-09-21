using System.Text.Json;

namespace Wrapp.Helpers;

/// <summary>
/// Type-coerced primitive readers over <see cref="JsonElement"/> with a
/// "return the default value on missing or wrong shape" contract.
/// Mirror of <see cref="JsonObjectExtensions"/> for the read-only
/// <c>JsonDocument</c>/<c>JsonElement</c> tree (used when responses come
/// back from REST APIs and we want to walk them without round-tripping
/// through a mutable <c>JsonObject</c>).
/// </summary>
/// <remarks>
/// <para>
/// Replaces the recurring boilerplate in
/// <see cref="Wrapp.Services.AppInventoryService"/>'s ContentDownload partial:
/// <code>
/// var name = obj.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
/// </code>
/// becomes <c>obj.GetStringOr("displayName")</c>.
/// </para>
/// <para>
/// All methods are <c>must-stay-silent on shape mismatch</c> by design --
/// the documented default-value fallback is the explicit recovery path,
/// and logging would create noise on every legitimate missing-field read
/// (which is normal for partial Graph responses).
/// </para>
/// </remarks>
public static class JsonElementExtensions
{
    /// <summary>
    /// Reads a string-typed property. Returns <paramref name="defaultValue"/>
    /// when the key is missing, the value is null, or the value isn't a
    /// string token.
    /// </summary>
    public static string GetStringOr(this JsonElement el, string key, string defaultValue = "")
        => el.TryGetProperty(key, out var v) ? v.GetString() ?? defaultValue : defaultValue;

    /// <summary>
    /// Reads an int-typed property. Returns <paramref name="defaultValue"/>
    /// when the key is missing or the value isn't a number token.
    /// </summary>
    public static int GetIntOr(this JsonElement el, string key, int defaultValue = 0)
    {
        if (!el.TryGetProperty(key, out var v)) return defaultValue;
        return v.TryGetInt32(out var n) ? n : defaultValue;
    }

    /// <summary>
    /// Reads a long-typed property. Returns <paramref name="defaultValue"/>
    /// when the key is missing or the value isn't a number token.
    /// </summary>
    public static long GetInt64Or(this JsonElement el, string key, long defaultValue = 0)
    {
        if (!el.TryGetProperty(key, out var v)) return defaultValue;
        return v.TryGetInt64(out var n) ? n : defaultValue;
    }

    /// <summary>
    /// Reads a bool-typed property. Returns <paramref name="defaultValue"/>
    /// when the key is missing or the value isn't a bool token.
    /// </summary>
    public static bool GetBoolOr(this JsonElement el, string key, bool defaultValue = false)
    {
        if (!el.TryGetProperty(key, out var v)) return defaultValue;
        return v.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _                   => defaultValue,
        };
    }
}
