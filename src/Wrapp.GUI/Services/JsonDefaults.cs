using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wrapp.Services;

/// <summary>
/// Canonical <see cref="JsonSerializerOptions"/> instances used across the app.
/// Centralised so that JSON conventions (indentation, escaping, case handling)
/// don't drift between services — drift across a settings-heavy codebase is an
/// audit red flag and a real source of bugs when fields round-trip differently
/// depending on which service touches them.
///
/// <para>Three variants:</para>
/// <list type="bullet">
/// <item><b>Pretty</b> — indented output with default (strict) escaping.
/// Use for files that are primarily user-facing (logs, manifests) where the
/// browser-safe escape of <c>&lt;</c> / <c>&gt;</c> / <c>&amp;</c> is desired.</item>
/// <item><b>PrettyUnsafe</b> — indented output with
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>. Use when the file
/// stores values that must round-trip byte-exact (Base64 keys, config JSON
/// consumed by PowerShell, any payload containing <c>+</c> or <c>/</c>).</item>
/// <item><b>CaseInsensitive</b> — read-side leniency for config files authored
/// by humans. Use on deserialize only.</item>
/// </list>
/// </summary>
public static class JsonDefaults
{
    // Enum handling: serialise as the Pascal-case member name (not the
    // integer ordinal), and accept any casing on read. This is what keeps
    // the string-based Config.json and PowerShell-side comparisons
    // (`$_.UpdateMode -eq 'Create'`) round-tripping byte-exact after C#
    // promotes these properties from `string` to a real enum type.
    //
    // Custom converter rather than stock JsonStringEnumConverter because
    // .NET 8's stock converter's case-insensitivity behaviour depends on
    // several interacting options and is easy to get wrong silently. This
    // is always case-insensitive on read, always canonical-cased on write,
    // and throws cleanly on unknown values so data corruption surfaces at
    // parse time instead of silently mapping to a default.
    private static readonly CaseInsensitiveEnumConverterFactory EnumConverter = new();

    private sealed class CaseInsensitiveEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(
                typeof(EnumConverter<>).MakeGenericType(typeToConvert))!;

        private sealed class EnumConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException($"Expected string for enum {typeof(T).Name}; got {reader.TokenType}.");
                var s = reader.GetString() ?? string.Empty;
                if (Enum.TryParse<T>(s, ignoreCase: true, out var value))
                    return value;
                throw new JsonException(
                    $"'{s}' is not a valid {typeof(T).Name}. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames<T>())}.");
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
                => writer.WriteStringValue(value.ToString());
        }
    }

    /// <summary>Indented output, strict (browser-safe) escaping, string-form enums.</summary>
    public static readonly JsonSerializerOptions Pretty = BuildPretty(
        encoder: null);

    /// <summary>
    /// Indented output, UnsafeRelaxedJsonEscaping. Preserves <c>+</c>, <c>/</c>,
    /// and other non-HTML-sensitive characters as-is so Base64 strings and
    /// PowerShell-consumed payloads round-trip correctly. Also applies the
    /// global <see cref="JsonStringEnumConverter"/> so enum-typed properties
    /// serialise as their member name string.
    /// </summary>
    public static readonly JsonSerializerOptions PrettyUnsafe = BuildPretty(
        encoder: JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    /// <summary>
    /// Deserialisation defaults: tolerant of case mismatches on property names
    /// so hand-edited config JSON with inconsistent casing still loads.
    /// Enum values are also accepted case-insensitively.
    /// </summary>
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { EnumConverter },
    };

    private static JsonSerializerOptions BuildPretty(JavaScriptEncoder? encoder)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        if (encoder is not null) opts.Encoder = encoder;
        opts.Converters.Add(EnumConverter);
        return opts;
    }
}
