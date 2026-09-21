using System.Text;
using System.Text.Json;

namespace Wrapp.Services;

/// <summary>
/// <para>
/// <b>PRESENTATION-ONLY JWT INSPECTOR.</b>  Decodes a JWT's payload for
/// display in the account flyout and similar UI. <b>DOES NOT</b> verify the
/// signature, issuer, audience, or expiry. The result is <i>never</i> a
/// trustworthy source of identity or authorisation.
/// </para>
/// <para>
/// <b>Never use this class to make a security decision.</b>  If you are
/// tempted to ("is this token for the right tenant?", "is the token still
/// valid?", "does the user have claim X?"), use MSAL's
/// <c>AuthenticationResult</c> properties (which come from a validated
/// token) or <c>Microsoft.IdentityModel.Tokens.JwtSecurityTokenHandler</c>.
/// Method names in this class end in <c>ForDisplay</c> so any call site
/// outside of UI code is a smell.
/// </para>
/// </summary>
public static class JwtDecoder
{
    /// <summary>
    /// Decodes a JWT payload for <b>display only</b>. Returns an empty list
    /// if the token is null, empty, malformed, or fails to decode. <b>Never</b>
    /// use the returned claims to make a security decision -- the signature
    /// is not verified.
    /// </summary>
    public static List<KeyValuePair<string, string>> DecodePayloadForDisplay(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return new();

        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return new();

            var json = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(json);
            return FlattenClaims(doc.RootElement);
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Extracts a single claim value from a JWT payload for <b>display only</b>.
    /// Returns null if the token is null/malformed or the claim is absent.
    /// <b>Never</b> use the returned value to make a security decision --
    /// the signature is not verified.
    /// </summary>
    public static string? GetClaimForDisplay(string? jwt, string claimName)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;

        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            var json = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(claimName, out var value))
                return value.ToString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static List<KeyValuePair<string, string>> FlattenClaims(JsonElement root)
    {
        var result = new List<KeyValuePair<string, string>>();

        foreach (var prop in root.EnumerateObject())
        {
            var display = FormatClaimValue(prop.Name, prop.Value);
            result.Add(new KeyValuePair<string, string>(prop.Name, display));
        }

        return result;
    }

    private static string FormatClaimValue(string name, JsonElement value)
    {
        // Unix timestamps -> readable date
        if (name is "exp" or "iat" or "nbf" or "xms_tcdt" or "auth_time"
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long epoch))
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }

        // Arrays -> joined with semicolons
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = new List<string>();
            foreach (var item in value.EnumerateArray())
                items.Add(item.ToString());
            return string.Join("; ", items);
        }

        // Scopes (space-separated string) -> newline-separated
        if (name == "scp" && value.ValueKind == JsonValueKind.String)
        {
            var scp = value.GetString() ?? "";
            return scp.Replace(" ", "\n");
        }

        return value.ToString();
    }

    private static string Base64UrlDecode(string base64Url)
    {
        var padded = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        var bytes = Convert.FromBase64String(padded);
        return Encoding.UTF8.GetString(bytes);
    }
}
