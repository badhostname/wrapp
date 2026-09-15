using System.IO;
using System.Text;

namespace Wrapp.Services;

/// <summary>
/// Single-component filename sanitiser: replaces every character invalid in a
/// Windows filename with <c>_</c> and trims the result. Strips path separators
/// (<c>\</c>, <c>/</c>) along with the rest, so callers should not pass full
/// paths -- use it on individual filename components only.
/// </summary>
/// <remarks>
/// <para>
/// Replaces two identical implementations that previously lived in
/// <see cref="EncryptionKeyStoreService"/> (<c>SanitizeName</c>) and
/// <see cref="Wrapp.ViewModels.InventoryViewModel"/> (<c>SanitizeFileName</c>).
/// </para>
/// <para>
/// The directory-aware sanitiser in <see cref="BundleService"/>.<c>Sanitize</c>
/// is intentionally NOT consolidated here: it preserves <c>\</c> so tokenised
/// directory templates like <c>{Company}\{Name}</c> still work. Different
/// contract, different name, different file -- on purpose.
/// </para>
/// </remarks>
public static class FileNameSanitizer
{
    /// <summary>
    /// Returns <paramref name="name"/> with every <see cref="Path.GetInvalidFileNameChars"/>
    /// character replaced by <c>_</c>, then trimmed of leading/trailing whitespace.
    /// Returns <see cref="string.Empty"/> for null or empty input.
    /// </summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }
}
