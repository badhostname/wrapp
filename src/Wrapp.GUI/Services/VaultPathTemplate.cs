using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Phase 16c. Token expander for Azure DevOps vault key paths. Uses
/// SINGLE-BRACE tokens (<c>{Tenant}</c>, <c>{AppId}</c>, &hellip;) to match
/// the path-style precedent in <see cref="BundleService.ResolveSubDirectory"/>;
/// metadata-style double-brace tokens (<c>{{Company}}</c>, &hellip;) are
/// reserved for the script-content substitution path in <c>ApplyTokens</c>.
/// <para>
/// Per-token sanitisation forbids path traversal (<c>..</c>), Windows-invalid
/// filename characters, and embedded path separators inside a single token's
/// value. The template itself preserves its literal forward slashes &mdash;
/// those are the folder boundaries the operator chose. Only token expansions
/// are sanitised, so the operator can build any path shape they want without
/// the helper second-guessing the template structure.
/// </para>
/// </summary>
public static class VaultPathTemplate
{
    /// <summary>
    /// Resolves <paramref name="template"/> against a key record. Returns
    /// <see cref="string.Empty"/> for null or empty templates (caller should
    /// guard with a default before calling). The optional <paramref name="author"/>
    /// override lets test code pass a deterministic value; production callers
    /// pass <c>null</c> and inherit <see cref="Environment.UserName"/>.
    /// </summary>
    public static string Resolve(string? template, EncryptionKeyInfo keys, string? author = null)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        var date = SystemClock.UtcNow.ToIsoDateOnly();
        author = string.IsNullOrEmpty(author) ? Environment.UserName : author;
        return template
            .Replace("{Tenant}",      Sanitize(keys.TenantId))
            .Replace("{AppId}",       Sanitize(keys.AppId))
            .Replace("{AppName}",     Sanitize(keys.DisplayName))
            .Replace("{PackageName}", Sanitize(keys.PackageName))
            .Replace("{Date}",        date)
            .Replace("{Author}",      Sanitize(author));
    }

    /// <summary>
    /// Operator-facing catalogue of supported tokens. The Settings UI uses
    /// this for the help text next to each template field so the list never
    /// drifts from the actual Resolve implementation. Keep in sync with the
    /// <see cref="Resolve"/> body.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedTokens = new[]
    {
        "{Tenant}",
        "{AppId}",
        "{AppName}",
        "{PackageName}",
        "{Date}",
        "{Author}",
    };

    /// <summary>
    /// Replaces path-traversal segments, embedded path separators, and
    /// Windows-invalid filename characters with <c>_</c>. A token VALUE that
    /// happens to contain <c>/</c> or <c>\</c> (e.g. an AppName typed as
    /// "My App / 1.0") collapses to a single segment so the template's
    /// folder structure stays predictable.
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Strip `..` first so the path-traversal segment is gone before the
        // single-char replacement below has a chance to turn `..` into `__`
        // by another route.
        var cleaned = value.Replace("..", "_");
        foreach (var bad in new[] { "/", "\\", ":", "*", "?", "\"", "<", ">", "|" })
            cleaned = cleaned.Replace(bad, "_");
        return cleaned;
    }
}
