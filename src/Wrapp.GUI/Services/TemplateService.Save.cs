using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Wrapp.Models;

namespace Wrapp.Services;

// ============================================================
// Write path: save templates from the UI. Custom templates are
// registered in manifest.json with BuiltIn:false so the update
// and reset logic never touches them. Built-in entries can
// never be overwritten or deleted from here.
//
// Token semantics (deliberate): content and field values are
// saved VERBATIM. {{Name}}-style tokens the user authored stay
// literal in the template file and expand only when the
// template is applied (LoadScriptTemplate/ApplyTokens). There
// is no reverse tokenization on save -- guessing which literal
// strings "should" be tokens corrupts templates silently.
// ============================================================

/// <summary>Which template family a name-collision check targets.</summary>
public enum TemplateKind { Script, Package, Assignment, Deployment }

/// <summary>Result of a save-time name-collision check.</summary>
public enum TemplateCollision { None, Custom, BuiltIn }

/// <summary>
/// One candidate field for a package template. <see cref="Checked"/> is mutable
/// so the save dialog's checkboxes can bind TwoWay without an extra view-model.
/// </summary>
public sealed class TemplateFieldChoice
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public bool Checked { get; set; }

    /// <summary>Display form for the dialog list.</summary>
    public string Display => string.IsNullOrEmpty(Value) ? "(empty)" : Value;
}

public static partial class TemplateService
{
    // -----------------------------------------------------------------------
    // Field selection for package templates
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fields that must never travel via a package template: identity, commands,
    /// and per-bundle state. Superset of the apply-time skip set in
    /// <see cref="ApplyPackageTemplate"/> -- anything listed there is dead weight
    /// in a saved template, and the additions (TenantId, versions, comments)
    /// are per-app values that would corrupt other bundles on apply.
    /// </summary>
    internal static readonly HashSet<string> PackageTemplateExcludedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        // NOTE: AppName is deliberately NOT excluded — it is offered as an
        // unchecked choice so a template can carry a name pattern (typically
        // with placeholders: "{{Company}} {{Name}}") when the user opts in.
        "TemplateName", "Description", "Assignments", "Deployments",
        "PackageId", "IconFile", "Icon", "IconPath",
        "InstallCommand", "UninstallCommand", "RepairCommand",
        "TenantId", "SiteCode", "ExistingAppID",
        "AppVersion", "SoftwareVersion", "Comment", "AppComment", "ReleaseDate",
        // Per-app note (seeded from the Notes preference template, not from
        // package templates) — same rule as Comment.
        "Notes",
    };

    /// <summary>
    /// Behavioral settings pre-checked in the save dialog -- the fields the
    /// built-in templates set (install experience, restart, runtime, targeting
    /// requirements). Metadata fields (Developer, Owner, URLs, ...) stay listed
    /// but unchecked so per-app values aren't captured by accident.
    /// </summary>
    internal static readonly HashSet<string> PackageTemplateCoreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        // Intune
        "PackageOption", "InstallExperience", "RestartBehavior",
        "MaximumInstallationTimeInMinutes", "AllowAvailableUninstall",
        "CompanyPortalFeaturedApp", "UseAzCopy", "AzCopyWindowStyle",
        "Architecture", "MinimumSupportedWindowsRelease",
        // SCCM
        "InstallationBehaviorType", "LogonRequirementType", "InstallBehavior",
        "UserInteractionMode", "RebootBehavior", "EstimatedRuntimeMins",
        "MaximumAllowedRuntimeMins", "SlowNetworkDeploymentMode", "ContentFallback",
    };

    /// <summary>
    /// Lists the fields of <paramref name="package"/> that can be stored in a
    /// template, with current values. Behavioral settings come first and are
    /// pre-checked; metadata fields follow unchecked. Scalars (string/int/bool)
    /// and entry collections (return codes, scope tags, categories,
    /// dependencies, supersedence, install behaviors) are offered -- exactly
    /// what <see cref="ApplyPackageTemplate"/> can apply back. When
    /// <paramref name="presetFrom"/> is given (update flow), the checks mirror
    /// the keys already present in that template file instead, so an update
    /// keeps the template's shape by default.
    /// </summary>
    public static List<TemplateFieldChoice> GetPackageTemplateFields(
        IPackageEntry package, TemplateInfo? presetFrom = null)
    {
        HashSet<string>? preset = null;
        if (presetFrom is not null)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(presetFrom.FilePath))?.AsObject() is { } node)
                    preset = new HashSet<string>(node.Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
            }
            catch { /* unreadable template -- fall back to the core defaults */ }
        }

        var choices = EnumerateTemplateProps(package.GetType())
            .Select(p => new TemplateFieldChoice
            {
                Name = p.Name,
                Value = DisplayValueOf(p, package),
                Checked = preset?.Contains(p.Name) ?? PackageTemplateCoreFields.Contains(p.Name),
            })
            .ToList();

        // Hierarchical templates: the package's assignments/deployments are
        // offered as ONE choice (unchecked by default — carrying targeting is
        // an explicit opt-in). Offered whenever children exist, or when the
        // preset template already carries them (so quick-save keeps them).
        var childKey = package is SCCMPackageEntry ? "Deployments" : "Assignments";
        var childCount = package switch
        {
            IntunePackageEntry i => i.Assignments.Count,
            SCCMPackageEntry s => s.Deployments.Count,
            _ => 0,
        };
        if (childCount > 0 || preset?.Contains(childKey) == true)
        {
            choices.Add(new TemplateFieldChoice
            {
                Name = childKey,
                Value = childCount == 1 ? "1 item" : $"{childCount} items",
                Checked = preset?.Contains(childKey) ?? false,
            });
        }

        return choices
            .OrderByDescending(f => f.Checked)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string DisplayValueOf(PropertyInfo p, IPackageEntry package)
    {
        var value = p.GetValue(package);
        if (value is System.Collections.ICollection col)
            return col.Count == 1 ? "1 item" : $"{col.Count} items";
        return value?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// True for the entry-collection properties templates can carry. The
    /// entry types keep UI-only state (<c>IsSelected</c>) behind
    /// <c>[JsonIgnore]</c>, so a straight JSON round-trip is clean.
    /// </summary>
    internal static bool IsTemplatableCollection(Type t) =>
        t.IsGenericType
        && t.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ObservableCollection<>)
        && t.GetGenericArguments()[0] is { } item
        && (item == typeof(TagEntry) || item == typeof(ReturnCodeEntry)
            || item == typeof(DependencyEntry) || item == typeof(SupersedenceEntry)
            || item == typeof(InstallBehaviorEntry));

    private static IEnumerable<PropertyInfo> EnumerateTemplateProps(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => p.PropertyType == typeof(string)
                     || p.PropertyType == typeof(int)
                     || p.PropertyType == typeof(bool)
                     || IsTemplatableCollection(p.PropertyType))
            .Where(p => !PackageTemplateExcludedFields.Contains(p.Name))
            .Where(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null);

    // -----------------------------------------------------------------------
    // Collision check (drives overwrite-confirm / built-in-refuse in the UI)
    // -----------------------------------------------------------------------

    public static TemplateCollision CheckCollision(TemplateKind kind, string name, string? categoryOrPlatform = null)
    {
        string fileName;
        try { fileName = SanitizeTemplateFileName(name) + FileExtension(kind); }
        catch (ArgumentException) { return TemplateCollision.None; }

        var relativePath = Path.Combine(ResolveSubDir(kind, categoryOrPlatform), fileName);
        var manifest = LoadManifest();
        if (manifest.Templates.TryGetValue(relativePath, out var entry))
            return entry.BuiltIn ? TemplateCollision.BuiltIn : TemplateCollision.Custom;

        // On-disk file with no manifest entry (pre-manifest install): treat as
        // potentially user-edited, i.e. custom -- the UI asks before overwrite.
        return File.Exists(Path.Combine(TemplateDir, relativePath))
            ? TemplateCollision.Custom
            : TemplateCollision.None;
    }

    private static string ResolveSubDir(TemplateKind kind, string? categoryOrPlatform) => kind switch
    {
        TemplateKind.Script => Path.Combine("Scripts", categoryOrPlatform
            ?? throw new ArgumentNullException(nameof(categoryOrPlatform))),
        TemplateKind.Package => Path.Combine("Packages", categoryOrPlatform
            ?? throw new ArgumentNullException(nameof(categoryOrPlatform))),
        TemplateKind.Assignment => "Assignments",
        TemplateKind.Deployment => "Deployments",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string FileExtension(TemplateKind kind)
        => kind == TemplateKind.Script ? ".ps1" : ".json";

    // -----------------------------------------------------------------------
    // Save methods
    // -----------------------------------------------------------------------

    /// <summary>
    /// Saves editor content as a script template under Scripts\{category}.
    /// A non-empty <paramref name="description"/> is written as the first-line
    /// <c>#</c> comment, which is where <see cref="LoadTemplateList"/> reads it back.
    /// </summary>
    public static TemplateInfo SaveScriptTemplate(string category, string name, string description, string content)
    {
        description = description.Trim();
        if (description.Length > 0 && !content.StartsWith($"# {description}"))
            content = $"# {description}\n{content}";

        return SaveTemplateCore(TemplateKind.Script, category, name, content, category);
    }

    /// <summary>
    /// Saves the chosen fields of <paramref name="package"/> as a sparse package
    /// template. Only <paramref name="fields"/> are written -- on apply, only
    /// keys present in the JSON overwrite the target package, so an unchecked
    /// field is left alone rather than reset.
    /// </summary>
    public static TemplateInfo SavePackageTemplate(
        string platform, string name, string description,
        IPackageEntry package, IReadOnlyCollection<string> fields)
    {
        var json = BuildPackageTemplateJson(name, description, package, fields);
        return SaveTemplateCore(TemplateKind.Package, platform, name, json, $"Packages/{platform}");
    }

    /// <summary>Saves an assignment row as a template (inverse of <see cref="LoadAssignmentTemplate"/>).</summary>
    public static TemplateInfo SaveAssignmentTemplate(string name, string description, AssignmentEntry entry)
    {
        var json = BuildAssignmentTemplateJson(name, description, entry);
        return SaveTemplateCore(TemplateKind.Assignment, null, name, json, "Assignments");
    }

    /// <summary>Saves a deployment row as a template (inverse of <see cref="LoadDeploymentTemplate"/>).</summary>
    public static TemplateInfo SaveDeploymentTemplate(string name, string description, SCCMDeploymentEntry entry)
    {
        var json = BuildDeploymentTemplateJson(name, description, entry);
        return SaveTemplateCore(TemplateKind.Deployment, null, name, json, "Deployments");
    }

    // M2: each save is a manifest read-modify-write; the cross-process lock
    // keeps a concurrent instance's cycle from interleaving (which would
    // silently unregister its template from the manifest).
    private static TemplateInfo SaveTemplateCore(
        TemplateKind kind, string? categoryOrPlatform,
        string name, string content, string listCategory)
    {
        TemplateInfo result = null!;
        CrossProcessLock.Run("Templates", () =>
            result = SaveTemplateCoreLocked(kind, categoryOrPlatform, name, content, listCategory));
        return result;
    }

    private static TemplateInfo SaveTemplateCoreLocked(
        TemplateKind kind, string? categoryOrPlatform,
        string name, string content, string listCategory)
    {
        name = name.Trim();
        var fileName = SanitizeTemplateFileName(name) + FileExtension(kind);
        var relativePath = Path.Combine(ResolveSubDir(kind, categoryOrPlatform), fileName);

        var manifest = LoadManifest();
        if (manifest.Templates.TryGetValue(relativePath, out var existing) && existing.BuiltIn)
            throw new InvalidOperationException(
                $"\"{name}\" matches the built-in template file {fileName}, which cannot be overwritten. Choose a different name.");

        var destPath = Path.Combine(TemplateDir, relativePath);
        WriteTemplate(destPath, content);
        manifest.Templates[relativePath] = new ManifestEntry { BuiltIn = false, Hash = ComputeHash(content) };
        SaveManifest(manifest);

        AppLogger.Info($"TemplateService: saved custom template {relativePath}");
        return new TemplateInfo(name, DescriptionOf(kind, content), destPath, listCategory);
    }

    private static string DescriptionOf(TemplateKind kind, string content)
    {
        if (kind == TemplateKind.Script)
        {
            var firstLine = content.Split('\n', 2)[0].TrimEnd('\r');
            return firstLine.StartsWith("# ") ? firstLine[2..].Trim() : string.Empty;
        }
        try
        {
            return JsonNode.Parse(content)?.AsObject() is { } node ? Str(node, "Description") : string.Empty;
        }
        catch { return string.Empty; }
    }

    // -----------------------------------------------------------------------
    // JSON builders (internal for unit tests)
    // -----------------------------------------------------------------------

    internal static string BuildPackageTemplateJson(
        string name, string description, IPackageEntry package, IReadOnlyCollection<string> fields)
    {
        var chosen = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
        var node = new JsonObject
        {
            ["TemplateName"] = name,
            ["Description"] = description,
        };

        foreach (var prop in EnumerateTemplateProps(package.GetType())
                     .Where(p => chosen.Contains(p.Name))
                     .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            node[prop.Name] = prop.GetValue(package) switch
            {
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                bool b => JsonValue.Create(b),
                // Entry collections serialize as JSON arrays ([JsonIgnore]
                // keeps UI-only entry state out). Saved VERBATIM like every
                // other value -- placeholders expand only on apply.
                { } collection when IsTemplatableCollection(prop.PropertyType)
                    => System.Text.Json.JsonSerializer.SerializeToNode(collection, prop.PropertyType),
                _ => null,
            };
        }

        // Hierarchical: children serialize through the shared sparse engine —
        // no AppName/PackageId (linkage is re-established on apply from the
        // target package), placeholders verbatim.
        if (chosen.Contains("Assignments") && package is IntunePackageEntry intune && intune.Assignments.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var a in intune.Assignments) arr.Add(BuildEntryTemplateJsonObject(a));
            node["Assignments"] = arr;
        }
        else if (chosen.Contains("Deployments") && package is SCCMPackageEntry sccm && sccm.Deployments.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var d in sccm.Deployments) arr.Add(BuildEntryTemplateJsonObject(d));
            node["Deployments"] = arr;
        }

        return node.ToJsonString(JsonDefaults.Pretty);
    }

    // Both entry kinds serialize through the shared sparse engine
    // (BuildEntryTemplateJsonObject) — the former hand-maintained key lists
    // had drifted and silently dropped fields (restart grace, UseLocalTime,
    // ApprovalRequired, …). Full field fidelity now, no lockstep lists.

    internal static string BuildAssignmentTemplateJson(string name, string description, AssignmentEntry e)
        => WrapEntryTemplate(name, description, e);

    internal static string BuildDeploymentTemplateJson(string name, string description, SCCMDeploymentEntry e)
        => WrapEntryTemplate(name, description, e);

    private static string WrapEntryTemplate(string name, string description, object entry)
    {
        var values = BuildEntryTemplateJsonObject(entry);
        var node = new JsonObject
        {
            ["TemplateName"] = name,
            ["Description"] = description,
        };
        foreach (var key in values.Select(kv => kv.Key).ToList())
        {
            var value = values[key];
            values.Remove(key);          // a JsonNode can only have one parent
            node[key] = value;
        }
        return node.ToJsonString(JsonDefaults.Pretty);
    }

    // -----------------------------------------------------------------------
    // Update in place (custom templates only)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Overwrites an existing custom script template with new content, keeping
    /// its file name and description. This is the quick "Save" for small tweaks
    /// -- no name re-entry, no new file.
    /// </summary>
    public static TemplateInfo UpdateScriptTemplate(TemplateInfo template, string content)
    {
        var description = template.Description.Trim();
        if (description.Length > 0 && !content.StartsWith($"# {description}"))
            content = $"# {description}\n{content}";

        return OverwriteTemplateFile(template, template.Name, content);
    }

    /// <summary>
    /// Overwrites an existing custom package template in place. The file name
    /// never changes -- <paramref name="name"/> only updates the TemplateName
    /// shown in pickers. This matters because display names and file names can
    /// legally differ for JSON templates (e.g. "Required - Group" in
    /// Required_Group.json), so re-deriving the file from the name would
    /// silently create a second template instead of updating this one.
    /// </summary>
    public static TemplateInfo UpdatePackageTemplate(
        TemplateInfo template, string name, string description,
        IPackageEntry package, IReadOnlyCollection<string> fields)
    {
        var json = BuildPackageTemplateJson(name, description, package, fields);
        return OverwriteTemplateFile(template, name, json);
    }

    private static TemplateInfo OverwriteTemplateFile(TemplateInfo template, string displayName, string content)
    {
        TemplateInfo result = null!;
        CrossProcessLock.Run("Templates", () =>
            result = OverwriteTemplateFileLocked(template, displayName, content));
        return result;
    }

    private static TemplateInfo OverwriteTemplateFileLocked(TemplateInfo template, string displayName, string content)
    {
        var rel = Path.GetRelativePath(TemplateDir, template.FilePath);
        if (rel.StartsWith("..") || Path.IsPathRooted(rel))
            throw new InvalidOperationException("Template file is outside the template directory.");

        var manifest = LoadManifest();
        if (manifest.Templates.TryGetValue(rel, out var entry) && entry.BuiltIn)
            throw new InvalidOperationException(
                $"\"{template.Name}\" is a built-in template and cannot be updated. Save it under a new name instead.");

        WriteTemplate(template.FilePath, content);
        manifest.Templates[rel] = new ManifestEntry { BuiltIn = false, Hash = ComputeHash(content) };
        SaveManifest(manifest);

        AppLogger.Info($"TemplateService: updated custom template {rel}");
        return template with { Name = displayName };
    }

    // -----------------------------------------------------------------------
    // Delete (custom templates only)
    // -----------------------------------------------------------------------

    /// <summary>True when the template is tracked as built-in in the manifest.</summary>
    public static bool IsBuiltInTemplate(TemplateInfo template)
    {
        var rel = Path.GetRelativePath(TemplateDir, template.FilePath);
        return LoadManifest().Templates.TryGetValue(rel, out var entry) && entry.BuiltIn;
    }

    /// <summary>
    /// Deletes a custom template file and its manifest entry. Refuses built-ins
    /// and anything outside <see cref="TemplateDir"/>. Returns true on success.
    /// </summary>
    public static bool DeleteCustomTemplate(TemplateInfo template)
    {
        var deleted = false;
        CrossProcessLock.Run("Templates", () => deleted = DeleteCustomTemplateLocked(template));
        return deleted;
    }

    private static bool DeleteCustomTemplateLocked(TemplateInfo template)
    {
        var rel = Path.GetRelativePath(TemplateDir, template.FilePath);
        if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return false;

        var manifest = LoadManifest();
        if (manifest.Templates.TryGetValue(rel, out var entry) && entry.BuiltIn) return false;

        try
        {
            if (File.Exists(template.FilePath)) File.Delete(template.FilePath);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"TemplateService: failed to delete {rel}: {ex.Message}");
            return false;
        }

        if (manifest.Templates.Remove(rel)) SaveManifest(manifest);
        AppLogger.Info($"TemplateService: deleted custom template {rel}");
        return true;
    }

    // -----------------------------------------------------------------------
    // Filename sanitization
    // -----------------------------------------------------------------------

    /// <summary>
    /// Turns a user-entered template name into a safe file name (no extension).
    /// Spaces become underscores (mirrors how <see cref="LoadTemplateList"/>
    /// displays ps1 names), invalid path characters become underscores, and
    /// the result can never escape the template directory. Throws
    /// <see cref="ArgumentException"/> when nothing usable remains.
    /// </summary>
    internal static string SanitizeTemplateFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            sb.Append(ch == ' ' || invalid.Contains(ch) ? '_' : ch);

        // Collapse runs of underscores, trim leading/trailing separators+dots
        var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_{2,}", "_")
            .Trim('_', '.');

        if (collapsed.Length == 0 || collapsed.All(c => c == '.'))
            throw new ArgumentException("Template name must contain at least one usable character.", nameof(name));

        return collapsed.Length > 60 ? collapsed[..60].Trim('_', '.') : collapsed;
    }
}
