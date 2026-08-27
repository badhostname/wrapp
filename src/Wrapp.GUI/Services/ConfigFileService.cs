using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Loads and saves Config.json, mapping between the JSON dict-based structure
/// and the UI-friendly ObservableCollection-based AppConfigModel.
/// </summary>
public static partial class ConfigFileService
{
    // Uses PrettyUnsafe so ClientSecret sentinels, Base64 payloads, and
    // PowerShell-consumed config values round-trip byte-exact.
    private static readonly JsonSerializerOptions _writeOptions = JsonDefaults.PrettyUnsafe;

    // -----------------------------------------------------------------------
    // Load
    // -----------------------------------------------------------------------

    /// <summary>
    /// Current bundle config schema version. Incremented when a breaking format
    /// change is made. Bundles saved by a newer app version (higher schema)
    /// are refused on load to prevent silent data loss on downgrade.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// SEC-8: hard ceiling on Config.json. Deserialization holds up to three
    /// copies of the text plus the node tree on the UI thread; a real bundle
    /// config is tens of KB, so anything near this cap is corrupt or hostile
    /// and gets a clear refusal instead of an OutOfMemoryException.
    /// </summary>
    public const long MaxConfigJsonBytes = 20 * 1024 * 1024;

    public static async Task<AppConfigModel> LoadAsync(string path)
    {
        var size = new FileInfo(path).Length;
        if (size > MaxConfigJsonBytes)
            throw new InvalidOperationException(
                $"Config.json is {size / 1024 / 1024} MB — larger than the {MaxConfigJsonBytes / 1024 / 1024} MB limit. " +
                "The file is likely corrupt; restore it from git history (History view) or a backup.");

        var json = await File.ReadAllTextAsync(path);
        return DeserializeFromJson(json);
    }

    public static AppConfigModel DeserializeFromJson(string json)
    {
        JsonObject? root;
        // The text the parser actually consumed -- starts as the user's input
        // and gets reassigned to the repaired version if the control-char
        // repair runs. Used by the outer catch to extract a line snippet.
        string parsed = json;

        // Cheap prescan: walk the document once tracking string-literal state
        // and detect any raw 0x00..0x1F that would make System.Text.Json throw.
        // Clean documents (the warm path) skip the repair branch and the
        // exception-driven retry entirely -- a thrown JsonException is the
        // expensive part. Older bundles with raw CR/LF/tab inside string
        // literals get repaired upfront before the parse is attempted.
        if (HasRawControlCharInsideString(json))
        {
            AppLogger.Warn("Config: raw control characters detected inside JSON string literal; attempting repair.");
            parsed = EscapeRawControlCharsInsideStrings(json);
            AppLogger.Info("Config: repair successful -- raw control characters escaped.");
        }

        try
        {
            root = JsonNode.Parse(parsed)?.AsObject();
        }
        catch (System.Text.Json.JsonException jx)
        {
            // Either the initial parse hit something the repair filter didn't
            // recognise (unclosed string, missing comma, invalid escape) OR
            // the repair ran but the post-repair parse still failed because
            // there was a deeper structural problem. Either way: enrich with
            // the offending line so the user can fix the JSON by hand.
            DumpHexContext(parsed, jx);
            var snippet = ExtractLineSnippet(parsed, jx);
            AppLogger.Warn($"Config: parse failed. {jx.Message} | snippet: {snippet}");
            throw new InvalidOperationException(
                $"Config.json is malformed near line {(jx.LineNumber ?? -1) + 1}: {jx.Message}\n" +
                $"Offending line: {snippet}\n" +
                "Most common cause is a missing closing quote or comma. Open the file in a text editor and check the line above.",
                jx);
        }

        if (root is null)
            throw new InvalidOperationException("Config.json is not a JSON object.");

        // Forward-compat check: refuse to open a bundle saved by a newer app
        // version. Silent field-drop on downgrade would otherwise corrupt data
        // the next save. Legacy bundles with no SchemaVersion are treated as v1.
        // SEC-9: coerce through the string form so a string-typed
        // "SchemaVersion": "99" cannot slip past the guard (TryGetValue<int>
        // returns false for it, which used to skip the check entirely).
        if (root["SchemaVersion"] is JsonValue svNode
            && int.TryParse(svNode.ToString(), out var sv)
            && sv > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"This bundle was saved with a newer version of Wrapp (schema v{sv}). " +
                $"This build supports schema v{CurrentSchemaVersion}. Please upgrade.");
        }

        var model = new AppConfigModel();

        // App section
        if (root["App"] is JsonObject app)
            model.App = ParseApp(app);

        // Script section
        if (root["Script"] is JsonObject script)
            model.Script = ParseScript(script);

        // SCCMSite - dict with arbitrary site keys
        if (root["SCCMSite"] is JsonObject sccmSites)
        {
            foreach (var kv in sccmSites)
            {
                if (kv.Key == "Comment") continue;
                if (kv.Value is not JsonObject siteObj) continue;
                model.SccmSites.Add(ParseSCCMSite(kv.Key, siteObj));
            }
        }

        // IntuneTenant - dict with arbitrary tenant keys
        if (root["IntuneTenant"] is JsonObject intuneTenants)
        {
            foreach (var kv in intuneTenants)
            {
                if (kv.Key == "Comment") continue;
                if (kv.Value is not JsonObject tenantObj) continue;
                model.IntuneTenants.Add(ParseIntuneTenant(kv.Key, tenantObj));
            }
        }

        // Domain - dict with arbitrary domain keys
        if (root["Domain"] is JsonObject domains)
        {
            foreach (var kv in domains)
            {
                if (kv.Key == "Comment") continue;
                if (kv.Value is not JsonObject domainObj) continue;
                model.Domains.Add(ParseDomain(kv.Key, domainObj));
            }
        }

        // Post-load migration: move old tenant-level assignments to packages
        MigrateOldAssignmentsToPackages(root, model);
        MigrateOldDeploymentsToPackages(root, model);

        return model;
    }

    /// <summary>
    /// Migrates old-format assignments (stored on IntuneTenant entries) to packages.
    /// Old format: IntuneTenant.{tenantId}.Assignments[]
    /// New format: Script.IntunePackager.Packages[].Assignments[]
    /// </summary>

    // -----------------------------------------------------------------------
    // Save
    // -----------------------------------------------------------------------

    public static async Task SaveAsync(AppConfigModel model, string path)
    {
        var json = SerializeToJson(model);
        // Crash-safe write: a power loss during the save leaves either the
        // old or new complete Config.json on disk -- never a truncated one.
        // Bundle Config.json is git-tracked, so a torn write would surface as
        // an unintended commit diff on next save.
        await AtomicFile.WriteAllTextAsync(path, json);
    }

    public static string SerializeToJson(AppConfigModel model)
        => SerializeToJsonCore(model);

    private static string SerializeToJsonCore(AppConfigModel model)
    {
        var root = new JsonObject
        {
            ["SchemaVersion"] = JsonValue.Create(CurrentSchemaVersion),
            ["App"]    = SerializeApp(model.App),
            ["Script"] = SerializeScript(model.Script, model.App.Name, model.App.Version)
        };

        // SCCMSite
        var sccmSitesObj = new JsonObject
        {
            ["Comment"] = JsonValue.Create("Site-specific SCCM related details")
        };
        foreach (var site in model.SccmSites)
            sccmSitesObj[site.Key] = SerializeSCCMSite(site);
        root["SCCMSite"] = sccmSitesObj;

        // IntuneTenant
        var tenantsObj = new JsonObject
        {
            ["Comment"] = JsonValue.Create("Tenant specific details/configuration")
        };
        foreach (var tenant in model.IntuneTenants)
            tenantsObj[tenant.Key] = SerializeIntuneTenant(tenant);
        root["IntuneTenant"] = tenantsObj;

        // Domain
        var domainsObj = new JsonObject
        {
            ["Comment"] = JsonValue.Create("Domain specific details - These particular values are appropriate for martigo AVD-OAM support")
        };
        foreach (var domain in model.Domains)
            domainsObj[domain.Key] = SerializeDomain(domain);
        root["Domain"] = domainsObj;

        return root.ToJsonString(_writeOptions);
    }

    // -----------------------------------------------------------------------
    // Parse helpers
    // -----------------------------------------------------------------------


    /// </summary>
    /// <remarks>
    /// Phase 15 (S-6): returns a <see cref="SecureString"/> or <c>null</c>.
    /// The sentinel ("ref:settings") and empty values both load as <c>null</c>
    /// because the real cipher lives in settings.json and is rehydrated later
    /// into <see cref="IntuneTenantEntry.ClientSecretCipher"/>. Anything else
    /// is wrapped as a SecureString for legacy bundles that may carry an
    /// in-bundle plaintext value (rare; SettingsViewModel still re-encrypts
    /// such values on the next save).
    /// </remarks>
    private static SecureString? ResolveClientSecretOnLoad(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (string.Equals(value, ClientSecretSentinel, StringComparison.Ordinal)) return null;
        return SecretProtection.ToSecureString(value);
    }

    /// <summary>
    /// Fast-path prescan companion to <see cref="EscapeRawControlCharsInsideStrings"/>.
    /// Walks the document tracking string-literal state and returns true if any
    /// raw control byte (<c>0x00..0x1F</c>) appears inside a <c>"..."</c> --
    /// the exact condition that makes <see cref="System.Text.Json"/> throw.
    /// Clean documents return false in one pass and avoid the cost of the
    /// exception-driven repair retry on the warm path.
    /// </summary>
    internal static bool HasRawControlCharInsideString(string json)
    {
        bool inString = false;
        bool escape   = false;
        foreach (var ch in json)
        {
            if (inString)
            {
                // A raw control byte fails the parse regardless of escape
                // state -- either inside a string literal directly or as the
                // target of a stray backslash. Either way: repair it.
                if (ch < 0x20) return true;
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"')  inString = false;
            }
            else if (ch == '"')
            {
                inString = true;
            }
        }
        return false;
    }

    /// <summary>
    /// Scans a JSON document and escapes any raw control characters
    /// (<c>0x00..0x1F</c>) that appear inside string literals. The JSON
    /// spec requires CR / LF / tab and similar control bytes inside a
    /// <c>"..."</c> to be written as <c>\r</c> / <c>\n</c> / <c>\t</c> /
    /// <c>\u00XX</c>. <see cref="System.Text.Json"/> rejects the raw
    /// forms with an exception; older Wrapp versions and some manual
    /// editors produced them anyway for multi-line values like
    /// embedded PowerShell scripts or long descriptions. This repair
    /// step only mutates bytes inside string literals — structural
    /// whitespace outside strings is left alone — so clean documents
    /// pass through untouched.
    /// </summary>
    internal static string EscapeRawControlCharsInsideStrings(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length + 32);
        bool inString = false;
        bool escape   = false;
        foreach (var ch in json)
        {
            if (inString)
            {
                if (escape)
                {
                    // Previous char was a backslash. In well-formed JSON the
                    // target is one of " \ / b f n r t u. Real-world corrupt
                    // files can land a raw control byte here (a stray \ then
                    // an embedded CR from a manually-edited multi-line value).
                    // The earlier behaviour appended the raw byte verbatim,
                    // which left an unescaped control char inside the string
                    // -- the parser then drifted, mistook a later " for the
                    // string terminator, and reported the next field name as
                    // 'X is invalid after a value'. Promote those stragglers
                    // to a valid JSON escape so the second parse can succeed.
                    switch (ch)
                    {
                        case '\r': sb.Append('r'); break;
                        case '\n': sb.Append('n'); break;
                        case '\t': sb.Append('t'); break;
                        default:
                            if (ch < 0x20)
                            {
                                // Can't form a valid 1-char escape -- rewrite
                                // the leading \ we already emitted as the
                                // start of a \uXXXX escape covering this byte.
                                sb.Length -= 1;
                                sb.Append($"\\u{(int)ch:X4}");
                            }
                            else
                            {
                                sb.Append(ch);
                            }
                            break;
                    }
                    escape = false;
                    continue;
                }
                switch (ch)
                {
                    case '\\':
                        sb.Append(ch);
                        escape = true;
                        break;
                    case '"':
                        sb.Append(ch);
                        inString = false;
                        break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append($"\\u{(int)ch:X4}");
                        else            sb.Append(ch);
                        break;
                }
            }
            else
            {
                if (ch == '"') inString = true;
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the line at <c>jx.LineNumber</c> from <paramref name="text"/>
    /// (System.Text.Json reports 0-indexed line numbers; the 1-indexed
    /// "human" line is <c>LineNumber + 1</c>). Long lines are truncated to
    /// keep error messages readable. Returns "(unknown)" if the line index
    /// is out of range so the message still renders.
    /// </summary>
    private static string ExtractLineSnippet(string text, System.Text.Json.JsonException jx)
    {
        try
        {
            var targetLine = (int)Math.Min(jx.LineNumber ?? 0, int.MaxValue);
            int curLine = 0;
            int lineStart = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (curLine == targetLine && (i == text.Length || text[i] == '\n'))
                {
                    var line = text.Substring(lineStart, i - lineStart).TrimEnd('\r');
                    return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
                }
                if (i < text.Length && text[i] == '\n')
                {
                    curLine++;
                    lineStart = i + 1;
                }
            }
        }
        // must-stay-silent: best-effort diagnostic helper. The "(unknown)"
        // string is the documented sentinel.
        catch { }
        return "(unknown)";
    }

    /// <summary>
    /// Logs a 64-byte hex window around a JSON parser error position so we
    /// can see exactly what bytes the parser tripped on. Used as the last
    /// line of defence when repair runs but the second parse still fails --
    /// gives us ground truth without asking users to dump bytes by hand.
    /// </summary>
    private static void DumpHexContext(string text, System.Text.Json.JsonException jx)
    {
        try
        {
            // BytePosition counts from start of file (long). We want a window
            // of ±32 bytes around it. Map to char index assuming ASCII-dominant
            // content (Config.json effectively is); high bytes shift the offset
            // slightly but the window is wide enough to still show the spot.
            var pos = (int)Math.Min(jx.BytePositionInLine ?? 0, int.MaxValue);
            // BytePositionInLine is line-relative; convert to absolute by
            // walking forward through line breaks up to LineNumber.
            var line = (int)Math.Min(jx.LineNumber ?? 0, int.MaxValue);
            int absPos = 0;
            for (int i = 0, lf = 0; i < text.Length && lf < line; i++)
            {
                if (text[i] == '\n') { lf++; absPos = i + 1; }
            }
            absPos += pos;
            absPos = Math.Clamp(absPos, 0, Math.Max(0, text.Length - 1));

            int start = Math.Max(0, absPos - 32);
            int end   = Math.Min(text.Length, absPos + 32);
            var window = text.Substring(start, end - start);

            var hex = new System.Text.StringBuilder();
            var ascii = new System.Text.StringBuilder();
            foreach (var c in window)
            {
                hex.Append($"{(int)c:X2} ");
                ascii.Append(c < 0x20 || c > 0x7E ? '.' : c);
            }
            AppLogger.Warn(
                $"Config: repair output still won't parse at line {line} byte {pos} (abs ~{absPos}). " +
                $"Window [{start}..{end}): hex={hex.ToString().TrimEnd()} | ascii={ascii}");
        }
        catch
        {
            // Diagnostic logging must never mask the underlying parse error.
        }
    }

    private static JsonArray ParseRawArray(IEnumerable<string> rawJsonItems)
    {
        var arr = new JsonArray();
        foreach (var raw in rawJsonItems)
        {
            try
            {
                var node = JsonNode.Parse(raw);
                if (node is not null) arr.Add(node);
            }
            catch
            {
                // Skip malformed entries
            }
        }
        return arr;
    }
}