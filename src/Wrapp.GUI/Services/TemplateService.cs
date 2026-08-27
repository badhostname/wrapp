using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Manages templates for scripts, packages, assignments, and deployments.
/// Templates live in %LOCALAPPDATA%\Wrapp\Templates\ and can be customized by the user.
/// Built-in templates are copied from embedded resources on first run.
/// A manifest.json tracks which files are built-in vs custom and their content hashes
/// so that app updates can refresh unmodified built-ins while preserving user edits.
/// </summary>
public static partial class TemplateService
{
    /// <summary>Template root. Internal setter lets tests redirect to a temp directory.</summary>
    public static string TemplateDir { get; internal set; }
        = Path.Combine(PlatformConfig.WrappRoot, "Templates");

    private static string ManifestPath => Path.Combine(TemplateDir, "manifest.json");

    // -----------------------------------------------------------------------
    // Manifest model
    // -----------------------------------------------------------------------

    private class TemplateManifest
    {
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, ManifestEntry> Templates { get; set; } = new();
    }

    private class ManifestEntry
    {
        public bool BuiltIn { get; set; }
        public string Hash { get; set; } = string.Empty;
    }

    // -----------------------------------------------------------------------
    // Bootstrap: copy/update built-in templates using manifest tracking
    // -----------------------------------------------------------------------

    // M2: bootstrap is a long read-modify-write over manifest.json; run it
    // whole under the cross-process templates lock so a concurrent instance's
    // save/delete can't interleave inside it.
    public static void EnsureBuiltInTemplates()
        => CrossProcessLock.Run("Templates", EnsureBuiltInTemplatesCore);

    private static void EnsureBuiltInTemplatesCore()
    {
        var manifest = LoadManifest();
        var changed = false;

        foreach (var (relativePath, resourceName) in GetBuiltInTemplates())
        {
            var embeddedContent = ReadEmbeddedResource(resourceName);
            if (embeddedContent is null) continue;
            changed |= SyncTrackedTemplate(manifest, relativePath, embeddedContent, "built-in");
        }

        manifest.Version = AppInfo.Version;
        if (changed) SaveManifest(manifest);
    }

    /// <summary>
    /// Synchronizes ONE hash-tracked template (embedded built-in or org-pack
    /// file) into the user's template directory:
    /// new file → written + registered; source changed + user did NOT edit
    /// the on-disk copy → auto-updated; user edited → preserved (manifest
    /// hash still advances so later comparisons use the new source).
    /// Returns true when the manifest changed.
    /// </summary>
    private static bool SyncTrackedTemplate(
        TemplateManifest manifest, string relativePath, string content, string sourceLabel)
    {
        var destPath = Path.Combine(TemplateDir, relativePath);
        var sourceHash = ComputeHash(content);

        if (!File.Exists(destPath))
        {
            WriteTemplate(destPath, content);
            manifest.Templates[relativePath] = new ManifestEntry { BuiltIn = true, Hash = sourceHash };
            return true;
        }

        if (!manifest.Templates.TryGetValue(relativePath, out var entry))
        {
            // File exists but not in manifest (pre-manifest install).
            // Record it but don't overwrite -- treat as potentially user-edited.
            manifest.Templates[relativePath] = new ManifestEntry { BuiltIn = true, Hash = sourceHash };
            return true;
        }

        if (entry.Hash == sourceHash)
            return false; // source unchanged since last sync

        // Source changed (app update / new org pack). Check for user edits.
        var diskHash = ComputeHash(File.ReadAllText(destPath));
        if (diskHash == entry.Hash)
        {
            WriteTemplate(destPath, content);
            entry.Hash = sourceHash;
            AppLogger.Info($"TemplateService: auto-updated {sourceLabel} template {relativePath}");
        }
        else
        {
            entry.Hash = sourceHash;
            AppLogger.Info($"TemplateService: skipped update for user-modified template {relativePath}");
        }
        return true;
    }

    // -----------------------------------------------------------------------
    // Workstream O: org template pack (files shipped beside the exe)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Source folder for org-shipped templates. Mirrors the Templates layout
    /// (Scripts\Install, Packages\Intune, Assignments, ...). Internal setter
    /// lets tests redirect it.
    /// </summary>
    internal static string OrgTemplatePackDir { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "org-templates");

    /// <summary>
    /// Syncs <see cref="OrgTemplatePackDir"/> into the user's template
    /// directory with the same semantics as the embedded built-ins: entries
    /// are manifest-tracked (BuiltIn:true → protected from overwrite/delete
    /// in the pickers), unedited copies refresh when the org ships a new
    /// pack, technician-edited copies are preserved. No-op when the folder
    /// doesn't exist (public builds).
    /// </summary>
    public static void EnsureOrgTemplatePack()
        => CrossProcessLock.Run("Templates", EnsureOrgTemplatePackCore);

    private static void EnsureOrgTemplatePackCore()
    {
        if (!Directory.Exists(OrgTemplatePackDir)) return;

        var manifest = LoadManifest();
        var changed = false;
        var count = 0;

        foreach (var (relativePath, content) in EnumerateOrgPackFiles())
        {
            changed |= SyncTrackedTemplate(manifest, relativePath, content, "org-pack");
            count++;
        }

        if (changed)
        {
            manifest.Version = AppInfo.Version;
            SaveManifest(manifest);
        }
        if (count > 0)
            AppLogger.Info($"TemplateService: org template pack synced ({count} file(s))");
    }

    /// <summary>(relativePath, content) pairs from the org pack; .ps1/.json only.</summary>
    private static IEnumerable<(string RelativePath, string Content)> EnumerateOrgPackFiles()
    {
        foreach (var file in Directory.EnumerateFiles(OrgTemplatePackDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not ".ps1" and not ".json") continue;

            var relativePath = Path.GetRelativePath(OrgTemplatePackDir, file);
            if (relativePath.StartsWith("..")) continue; // never escape the pack

            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; }

            yield return (relativePath, content);
        }
    }

    /// <summary>
    /// Resets all built-in templates to the versions embedded in the current app.
    /// User-created (custom) templates are not affected.
    /// Returns the number of templates reset.
    /// </summary>
    public static int ResetBuiltInTemplates()
    {
        var count = 0;
        CrossProcessLock.Run("Templates", () => count = ResetBuiltInTemplatesCore());
        return count;
    }

    private static int ResetBuiltInTemplatesCore()
    {
        var manifest = LoadManifest();
        var builtIns = GetBuiltInTemplates();
        int count = 0;

        foreach (var (relativePath, resourceName) in builtIns)
        {
            var destPath = Path.Combine(TemplateDir, relativePath);
            var embeddedContent = ReadEmbeddedResource(resourceName);
            if (embeddedContent is null) continue;

            WriteTemplate(destPath, embeddedContent);
            var hash = ComputeHash(embeddedContent);
            manifest.Templates[relativePath] = new ManifestEntry { BuiltIn = true, Hash = hash };
            count++;
        }

        // Workstream O: the org pack participates in reset the same way --
        // "restore built-in templates" also restores org-shipped ones.
        if (Directory.Exists(OrgTemplatePackDir))
        {
            foreach (var (relativePath, content) in EnumerateOrgPackFiles())
            {
                WriteTemplate(Path.Combine(TemplateDir, relativePath), content);
                manifest.Templates[relativePath] = new ManifestEntry { BuiltIn = true, Hash = ComputeHash(content) };
                count++;
            }
        }

        manifest.Version = AppInfo.Version;
        SaveManifest(manifest);
        AppLogger.Info($"TemplateService: reset {count} built-in templates to defaults");
        return count;
    }

    // -----------------------------------------------------------------------
    // Manifest I/O
    // -----------------------------------------------------------------------

    private static TemplateManifest LoadManifest()
    {
        try
        {
            if (File.Exists(ManifestPath))
            {
                var json = File.ReadAllText(ManifestPath);
                return JsonSerializer.Deserialize<TemplateManifest>(json) ?? new TemplateManifest();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"TemplateService: failed to read manifest: {ex.Message}");
        }
        return new TemplateManifest();
    }

    private static void SaveManifest(TemplateManifest manifest)
    {
        try
        {
            Directory.CreateDirectory(TemplateDir);
            var json = JsonSerializer.Serialize(manifest, JsonDefaults.Pretty);
            // Crash-safe: power loss during save leaves either the old or new
            // complete manifest on disk. A truncated manifest would otherwise
            // make the next launch think every template needs re-bootstrapping.
            AtomicFile.WriteAllText(ManifestPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"TemplateService: failed to write manifest: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly (string Prefix, string SubDir)[] ResourceMappings =
    {
        ("Install.",           Path.Combine("Scripts", "Install")),
        ("Uninstall.",         Path.Combine("Scripts", "Uninstall")),
        ("PSADT.",             Path.Combine("Scripts", "PSADT")),
        ("Packages.Intune.",   Path.Combine("Packages", "Intune")),
        ("Packages.SCCM.",    Path.Combine("Packages", "SCCM")),
        ("Assignments.",       "Assignments"),
        ("Deployments.",       "Deployments"),
    };

    /// <summary>
    /// Enumerates all built-in templates from embedded resources.
    /// Returns (relativePath, resourceName) pairs.
    /// </summary>
    private static List<(string RelativePath, string ResourceName)> GetBuiltInTemplates()
    {
        var asm = Assembly.GetExecutingAssembly();
        var prefix = "Wrapp.Templates.";
        var results = new List<(string, string)>();

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix)) continue;
            var relative = resourceName[prefix.Length..];

            string? subDir = null;
            string? fileName = null;

            foreach (var (pfx, dir) in ResourceMappings)
            {
                if (relative.StartsWith(pfx))
                {
                    subDir = dir;
                    fileName = relative[pfx.Length..];
                    break;
                }
            }

            if (subDir is null || fileName is null) continue;

            var relativePath = Path.Combine(subDir, fileName);
            results.Add((relativePath, resourceName));
        }

        return results;
    }

    private static string? ReadEmbeddedResource(string resourceName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    private static void WriteTemplate(string destPath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllText(destPath, content);
    }

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    // -----------------------------------------------------------------------
    // Script templates
    // -----------------------------------------------------------------------

    /// <summary>
    /// Lists available script templates for the given category ("Install" or "Uninstall").
    /// </summary>
    public static List<TemplateInfo> GetScriptTemplates(string category)
    {
        var dir = Path.Combine(TemplateDir, "Scripts", category);
        return LoadTemplateList(dir, category);
    }

    /// <summary>
    /// Loads a script template, applies token replacement from app info, and returns the content.
    /// </summary>
    public static string LoadScriptTemplate(TemplateInfo template, AppSection app)
    {
        var content = File.ReadAllText(template.FilePath);
        return ApplyTokens(content, app);
    }

    // -----------------------------------------------------------------------
    // Package templates
    // -----------------------------------------------------------------------

    public static List<TemplateInfo> GetPackageTemplates(string platform)
    {
        var dir = Path.Combine(TemplateDir, "Packages", platform);
        return LoadTemplateList(dir, $"Packages/{platform}");
    }

    /// <summary>
    /// Applies a package template's fields to the given package entry, with
    /// placeholder expansion — the same <c>{{Name}}</c> semantics as script
    /// templates: values are stored verbatim at save time and every string the
    /// template writes (scalars, AppName when the template carries it, and the
    /// string members of collection entries) expands against
    /// <paramref name="app"/> here. Collections (return codes, scope tags,
    /// categories, dependencies, supersedence, install behaviors) REPLACE the
    /// target's collection; keys absent from the template leave the target
    /// untouched (sparse semantics unchanged). Skips identity fields
    /// (PackageId, icon, commands).
    /// </summary>
    public static void ApplyPackageTemplate(TemplateInfo template, IPackageEntry package, AppSection app)
    {
        var json = File.ReadAllText(template.FilePath);
        var node = JsonNode.Parse(json)?.AsObject();
        if (node is null) return;

        // Skip = the save-time exclusion list — ONE source of truth. A saved
        // template never contains excluded keys, so this only bites hand-
        // edited files trying to inject per-bundle state (TenantId, commands,
        // identity). AppName is NOT excluded: it only appears in templates the
        // user explicitly saved it into (opt-in checkbox), and carrying a
        // placeholder name pattern is exactly its use case. Assignments/
        // Deployments are in the list too — they're handled explicitly below,
        // not via reflection.
        var type = package.GetType();
        foreach (var kv in node)
        {
            if (PackageTemplateExcludedFields.Contains(kv.Key)) continue;
            var prop = type.GetProperty(kv.Key);
            if (prop is null || !prop.CanWrite) continue;

            try
            {
                if (prop.PropertyType == typeof(string))
                    prop.SetValue(package, ApplyTokens(kv.Value?.GetValue<string>() ?? string.Empty, app));
                else if (prop.PropertyType == typeof(int))
                    prop.SetValue(package, kv.Value?.GetValue<int>() ?? 0);
                else if (prop.PropertyType == typeof(bool))
                    prop.SetValue(package, kv.Value?.GetValue<bool>() ?? false);
                else if (TemplateService.IsTemplatableCollection(prop.PropertyType) && kv.Value is not null)
                {
                    var collection = System.Text.Json.JsonSerializer.Deserialize(
                        kv.Value.ToJsonString(), prop.PropertyType);
                    if (collection is System.Collections.IEnumerable entries)
                        foreach (var entry in entries)
                            ExpandEntryStrings(entry, app);
                    prop.SetValue(package, collection);
                }
            }
            catch { /* skip incompatible types */ }
        }

        // Hierarchical templates: a child array REPLACES the package's
        // assignments/deployments (same replace-not-merge contract as the
        // other collections); an absent key leaves them untouched. Children
        // run AFTER the scalar pass so their linkage picks up an AppName the
        // template itself may have just set.
        if (node["Assignments"] is JsonArray assignments && package is IntunePackageEntry intune)
        {
            intune.Assignments.Clear();
            foreach (var item in assignments)
            {
                if (item is not JsonObject o) continue;
                var entry = new AssignmentEntry();
                ApplyEntryTemplate(o, entry, app);
                entry.AppName = intune.AppName;
                entry.PackageId = intune.PackageId;
                intune.Assignments.Add(entry);
            }
        }
        else if (node["Deployments"] is JsonArray deployments && package is SCCMPackageEntry sccm)
        {
            sccm.Deployments.Clear();
            foreach (var item in deployments)
            {
                if (item is not JsonObject o) continue;
                var entry = new SCCMDeploymentEntry();
                ApplyEntryTemplate(o, entry, app);
                entry.AppName = sccm.AppName;
                entry.PackageId = sccm.PackageId;
                sccm.Deployments.Add(entry);
            }
        }
    }

    /// <summary>Expands placeholders in every writable string property of a
    /// collection entry (dependency app names, tag names, behavior display
    /// names, ...).</summary>
    private static void ExpandEntryStrings(object entry, AppSection app)
    {
        foreach (var p in entry.GetType().GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.PropertyType != typeof(string) || !p.CanRead || !p.CanWrite) continue;
            if (p.GetValue(entry) is string s && s.Length > 0)
                p.SetValue(entry, ApplyTokens(s, app));
        }
    }

    // -----------------------------------------------------------------------
    // Entry-template engine (assignments, deployments, and the child arrays
    // inside hierarchical package templates)
    //
    // One sparse reflection mechanic replaces the former hand-maintained
    // save/load key lists, which had drifted: they silently dropped
    // UseLocalTime, the restart-grace fields, ApprovalRequired and friends.
    // Save writes every non-default templatable value; apply overlays only the
    // keys present. AppName/PackageId are linkage, never template content.
    // -----------------------------------------------------------------------

    /// <summary>Keys never stored in (or applied from) an entry template:
    /// metadata plus per-package linkage.</summary>
    internal static readonly HashSet<string> EntryTemplateExcludedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "TemplateName", "Description", "AppName", "PackageId",
    };

    private static IEnumerable<System.Reflection.PropertyInfo> EnumerateEntryTemplateProps(Type type) =>
        type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Where(p => p.PropertyType == typeof(string)
                     || p.PropertyType == typeof(int)
                     || p.PropertyType == typeof(bool))
            .Where(p => !EntryTemplateExcludedFields.Contains(p.Name))
            .Where(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null);

    /// <summary>
    /// Serializes an assignment/deployment entry's templatable values (sparse:
    /// empty strings, false bools and zero ints are omitted, mirroring the
    /// former AddIfNotEmpty semantics — absent keys leave the target's own
    /// defaults alone on apply). Values are saved VERBATIM; placeholders
    /// expand only on apply.
    /// </summary>
    internal static JsonObject BuildEntryTemplateJsonObject(object entry)
    {
        var node = new JsonObject();
        foreach (var p in EnumerateEntryTemplateProps(entry.GetType())
                     .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            switch (p.GetValue(entry))
            {
                case string s when !string.IsNullOrWhiteSpace(s): node[p.Name] = s; break;
                case bool b when b: node[p.Name] = true; break;
                case int i when i != 0: node[p.Name] = i; break;
            }
        }
        return node;
    }

    /// <summary>
    /// Overlays a template object onto an entry. Mode-selector keys (Type,
    /// Intent, DeployAction, DeployPurpose) apply FIRST: their change handlers
    /// cascade defaults (e.g. Intent=required pre-fills ASAP deadlines) that
    /// the template's own values must then override, never the reverse.
    /// Placeholders in strings expand when <paramref name="app"/> is given.
    /// </summary>
    internal static void ApplyEntryTemplate(JsonObject node, object entry, AppSection? app)
    {
        var type = entry.GetType();
        var ordered = node
            .Where(kv => !EntryTemplateExcludedFields.Contains(kv.Key))
            .OrderBy(kv => kv.Key is "Type" or "Intent" or "DeployAction" or "DeployPurpose" ? 0 : 1)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in ordered)
        {
            var prop = type.GetProperty(kv.Key);
            if (prop is null || !prop.CanWrite) continue;
            if (prop.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null)
                continue; // hand-edited template must not write UI-only state

            try
            {
                if (prop.PropertyType == typeof(string))
                {
                    var s = kv.Value?.GetValue<string>() ?? string.Empty;
                    prop.SetValue(entry, app is null ? s : ApplyTokens(s, app));
                }
                else if (prop.PropertyType == typeof(int))
                    prop.SetValue(entry, kv.Value?.GetValue<int>() ?? 0);
                else if (prop.PropertyType == typeof(bool))
                    prop.SetValue(entry, kv.Value?.GetValue<bool>() ?? false);
            }
            catch { /* hand-edited value of an incompatible type — skip key */ }
        }
    }

    // -----------------------------------------------------------------------
    // Assignment templates
    // -----------------------------------------------------------------------

    public static List<TemplateInfo> GetAssignmentTemplates()
    {
        var dir = Path.Combine(TemplateDir, "Assignments");
        return LoadTemplateList(dir, "Assignments");
    }

    /// <summary>
    /// Loads an assignment template. When <paramref name="app"/> is given,
    /// placeholders in its string fields (labels, group IDs, filter names…)
    /// expand exactly like script/package templates.
    /// </summary>
    public static AssignmentEntry LoadAssignmentTemplate(TemplateInfo template, AppSection? app = null)
    {
        var entry = new AssignmentEntry();
        if (JsonNode.Parse(File.ReadAllText(template.FilePath))?.AsObject() is { } node)
            ApplyEntryTemplate(node, entry, app);
        return entry;
    }

    // -----------------------------------------------------------------------
    // Deployment templates
    // -----------------------------------------------------------------------

    public static List<TemplateInfo> GetDeploymentTemplates()
    {
        var dir = Path.Combine(TemplateDir, "Deployments");
        return LoadTemplateList(dir, "Deployments");
    }

    /// <summary>
    /// Loads a deployment template. When <paramref name="app"/> is given,
    /// placeholders in its string fields (labels, collections, comments…)
    /// expand exactly like script/package templates.
    /// </summary>
    public static SCCMDeploymentEntry LoadDeploymentTemplate(TemplateInfo template, AppSection? app = null)
    {
        var entry = new SCCMDeploymentEntry();
        if (JsonNode.Parse(File.ReadAllText(template.FilePath))?.AsObject() is { } node)
            ApplyEntryTemplate(node, entry, app);
        return entry;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<TemplateInfo> LoadTemplateList(string dir, string category)
    {
        var list = new List<TemplateInfo>();
        if (!Directory.Exists(dir)) return list;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not ".ps1" and not ".json") continue;

            var name = Path.GetFileNameWithoutExtension(file).Replace('_', ' ');
            var description = string.Empty;
            var childCount = 0;

            // Try to read description from JSON TemplateName/Description fields
            if (ext == ".json")
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                    if (node?["TemplateName"] is JsonValue tn)
                        name = tn.GetValue<string>();
                    if (node?["Description"] is JsonValue desc)
                        description = desc.GetValue<string>();
                    // Hierarchical package templates carry a child array.
                    childCount = (node?["Assignments"] as JsonArray)?.Count
                              ?? (node?["Deployments"] as JsonArray)?.Count
                              ?? 0;
                }
                catch { /* use filename as name */ }
            }
            else if (ext == ".ps1")
            {
                // Read first comment line as description
                try
                {
                    using var reader = new StreamReader(file);
                    var firstLine = reader.ReadLine();
                    if (firstLine?.StartsWith("# ") == true)
                        description = firstLine[2..].Trim();
                }
                catch { /* skip */ }
            }

            list.Add(new TemplateInfo(name, description, file, category, childCount));
        }

        return list.OrderBy(t => t.Name).ToList();
    }

    /// <summary>
    /// Expands template tokens (e.g. <c>{{Company}}</c>, <c>{{Name}}</c>, <c>{{Date}}</c>,
    /// <c>{{Author}}</c>) against the current <paramref name="app"/> values. Empty or null
    /// <paramref name="content"/> is returned as-is. Used by the script-template apply flow
    /// and by the Preferences-backed AddPackage metadata seeding path.
    /// </summary>
    public static string ApplyTokens(string content, AppSection app)
        // Workstream P: single shared resolver — the twelve built-ins plus any
        // custom placeholders configured in Settings / org defaults.
        => PlaceholderService.Expand(content, app);

    private static string Str(JsonObject node, string key, string fallback = "")
    {
        if (node[key] is JsonValue val)
        {
            try { return val.GetValue<string>(); }
            catch { return fallback; }
        }
        return fallback;
    }
}

/// <summary>
/// Describes a template file available for selection. <paramref name="ChildCount"/>
/// is the number of assignments/deployments a hierarchical package template
/// carries (0 for flat templates and non-package kinds) — it drives the badge
/// in the package template dropdowns.
/// </summary>
public record TemplateInfo(string Name, string Description, string FilePath, string Category, int ChildCount = 0)
{
    /// <summary>Dropdown-badge tooltip: what applying this template replaces.</summary>
    public string ChildBadgeTooltip =>
        $"Includes {ChildCount} saved assignment(s)/deployment(s) — applying this template replaces the package's current ones.";
}
