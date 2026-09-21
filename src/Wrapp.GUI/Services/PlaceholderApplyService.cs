using System.Linq;
using System.Text;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// One string field a replace-in-place pass can touch: a label for logging,
/// a getter, and a setter that mutates the normal observable property (so
/// dirty tracking, validation and git history behave as if the user typed).
/// </summary>
public sealed record PlaceholderFieldAccessor(string Label, Func<string> Get, Action<string> Set);

/// <summary>
/// The shared replace-in-place core. Expands every field
/// in an explicit per-scope map via <see cref="PlaceholderService.Expand(string, AppSection, out PlaceholderExpandReport)"/>,
/// aggregates the reports, shows ONE summary confirm dialog BEFORE mutating
/// anything (with a bold warning when sensitive plaintext would be written
/// into the bundle), and applies the changes only on confirm.
///
/// <para>Field maps are explicit per type - house style, mirrors the
/// serializers. No reflection: predictable, testable, and skips fields where
/// substitution is nonsense (ids, enums, booleans, icon/file paths).</para>
/// </summary>
public static class PlaceholderApplyService
{
    /// <summary>One computed field change (new value differs from the old).</summary>
    public sealed record PlaceholderFieldChange(PlaceholderFieldAccessor Field, string NewValue);

    /// <summary>
    /// Expands every field and aggregates the per-field reports. Pure - no
    /// mutation, no UI - so the decision path is unit-testable.
    /// </summary>
    public static IReadOnlyList<PlaceholderFieldChange> ComputeChanges(
        IEnumerable<PlaceholderFieldAccessor> fields,
        AppSection app,
        out PlaceholderExpandReport aggregate)
    {
        var total = new PlaceholderExpandReport();
        var changes = new List<PlaceholderFieldChange>();

        foreach (var field in fields)
        {
            var current = field.Get() ?? string.Empty;
            if (current.Length == 0) continue;

            var expanded = PlaceholderService.Expand(current, app, out var report);
            Merge(total, report);
            if (!string.Equals(expanded, current, StringComparison.Ordinal))
                changes.Add(new PlaceholderFieldChange(field, expanded));
        }

        aggregate = total;
        return changes;
    }

    /// <summary>Folds one field's report into the aggregate (names de-duplicated).</summary>
    public static void Merge(PlaceholderExpandReport aggregate, PlaceholderExpandReport report)
    {
        aggregate.Replaced += report.Replaced;
        MergeNames(aggregate.LeftEmpty, report.LeftEmpty);
        MergeNames(aggregate.LeftUnknown, report.LeftUnknown);
        MergeNames(aggregate.SensitiveReplaced, report.SensitiveReplaced);

        // Token mapping: same name across fields sums its occurrence count.
        foreach (var token in report.Tokens)
        {
            var existing = aggregate.Tokens.FirstOrDefault(
                t => t.Name.Equals(token.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) aggregate.Tokens.Add(token);
            else existing.Count += token.Count;
        }
    }

    /// <summary>Caps a resolved value for display (dialogs, Markdown summaries).</summary>
    internal static string TruncateValue(string value, int max = 80)
        => value.Length <= max ? value : value[..max] + "…";

    private static void MergeNames(List<string> into, List<string> from)
    {
        foreach (var name in from)
        {
            if (!into.Contains(name, StringComparer.OrdinalIgnoreCase))
                into.Add(name);
        }
    }

    /// <summary>
    /// The confirm-dialog body (Markdown). Separated from the dialog call so
    /// tests can assert the summary and the sensitive warning without UI.
    /// </summary>
    public static string BuildSummaryMarkdown(
        PlaceholderExpandReport aggregate, int changedFieldCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**{aggregate.Replaced}** placeholder occurrence(s) will be replaced " +
                      $"across **{changedFieldCount}** field(s).");

        // Token mapping - what each token becomes. Sensitive values are
        // redacted here exactly as in Settings > Placeholders.
        var replaced = aggregate.Tokens
            .Where(t => t.Outcome == PlaceholderTokenOutcome.Replaced).ToList();
        if (replaced.Count > 0)
        {
            sb.AppendLine();
            foreach (var t in replaced)
                sb.AppendLine($"- `{{{{{t.Name}}}}}` ×{t.Count} → " +
                              (t.IsSensitive ? "••••••••" : $"`{TruncateValue(t.Value)}`"));
        }

        if (aggregate.LeftEmpty.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Left as-is (no value yet): {string.Join(", ", aggregate.LeftEmpty.Select(n => $"`{{{{{n}}}}}`"))}");
        }
        if (aggregate.LeftUnknown.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Left as-is (unknown name): {string.Join(", ", aggregate.LeftUnknown.Select(n => $"`{{{{{n}}}}}`"))}");
        }
        if (aggregate.TouchedSensitive)
        {
            sb.AppendLine();
            sb.AppendLine($"**Warning: the sensitive value(s) of {string.Join(", ", aggregate.SensitiveReplaced.Select(n => $"`{{{{{n}}}}}`"))} " +
                          "will be written into the bundle as plaintext** (Config.json / script content). " +
                          "Anyone with access to the bundle can read them.");
        }

        sb.AppendLine();
        sb.AppendLine("Replaced values are inserted exactly as stored; this cannot be undone " +
                      "except by editing the fields back (scripts: File History).");
        return sb.ToString();
    }

    /// <summary>
    /// Body for the nothing-changed info dialog (still lists why nothing
    /// happened - empty-value and unknown names are the usual answer).
    /// </summary>
    public static string BuildNothingToReplaceMessage(PlaceholderExpandReport aggregate)
    {
        var sb = new StringBuilder();
        sb.Append("No placeholder was replaced.");
        if (aggregate.LeftEmpty.Count > 0)
            sb.Append($"\n\nKnown but empty (fill their values in Settings > Placeholders): " +
                      $"{string.Join(", ", aggregate.LeftEmpty.Select(n => "{{" + n + "}}"))}");
        if (aggregate.LeftUnknown.Count > 0)
            sb.Append($"\n\nUnknown names left untouched: " +
                      $"{string.Join(", ", aggregate.LeftUnknown.Select(n => "{{" + n + "}}"))}");
        if (aggregate.LeftEmpty.Count == 0 && aggregate.LeftUnknown.Count == 0)
            sb.Append(" No {{Name}} tokens were found in this scope.");
        return sb.ToString();
    }

    /// <summary>
    /// Full flow for field-map scopes: compute → confirm → apply. Returns true
    /// when changes were applied. <paramref name="confirm"/> is injectable for
    /// tests; production uses <see cref="FluentDialog.ConfirmMarkdownAsync"/>
    /// (Markdown so the sensitive warning renders bold).
    /// </summary>
    public static async Task<bool> ApplyAsync(
        string scopeTitle,
        IEnumerable<PlaceholderFieldAccessor> fields,
        AppSection app,
        Func<string, string, Task<bool>>? confirm = null,
        Func<string, string, Task>? info = null)
    {
        var changes = ComputeChanges(fields, app, out var aggregate);

        if (changes.Count == 0)
        {
            info ??= (title, message) => FluentDialog.ShowInfoAsync(title, message);
            await info($"Replace placeholders - {scopeTitle}", BuildNothingToReplaceMessage(aggregate));
            return false;
        }

        // Production shows the token-mapping preview dialog (badge per token,
        // count, resolved value, sensitive redacted); tests inject a text
        // confirm and get the equivalent Markdown summary.
        bool confirmed;
        if (confirm is not null)
            confirmed = await confirm(
                $"Replace placeholders - {scopeTitle}",
                BuildSummaryMarkdown(aggregate, changes.Count));
        else
            confirmed = await PlaceholderPreviewDialog.ConfirmAsync(
                $"Replace placeholders - {scopeTitle}", aggregate, changes.Count);
        if (!confirmed) return false;

        foreach (var change in changes)
            change.Field.Set(change.NewValue);

        AppLogger.Info($"Placeholders: replaced {aggregate.Replaced} occurrence(s) in " +
                       $"{changes.Count} field(s) ({scopeTitle})");
        return true;
    }

    // ------------------------------------------------------------------
    // Explicit per-scope field maps (NO reflection)
    // ------------------------------------------------------------------

    /// <summary>
    /// General scope: the bundle's <see cref="AppSection"/> string fields AND
    /// its table rows (detect-running processes, dependency list).
    /// Skipped on purpose: GUID (identity, auto-generated), IconFile (path
    /// managed by the icon pipeline), ScriptFramework (enum-like selector).
    /// </summary>
    public static IReadOnlyList<PlaceholderFieldAccessor> GeneralFields(AppSection app)
    {
        var fields = new List<PlaceholderFieldAccessor>
        {
            new("App.Company",    () => app.Company,    v => app.Company    = v),
            new("App.Name",       () => app.Name,       v => app.Name       = v),
            new("App.Comment",    () => app.Comment,    v => app.Comment    = v),
            new("App.Language",   () => app.Language,   v => app.Language   = v),
            new("App.EXEFile",    () => app.EXEFile,    v => app.EXEFile    = v),
            new("App.MSIFile",    () => app.MSIFile,    v => app.MSIFile    = v),
            new("App.URL",        () => app.URL,        v => app.URL        = v),
            new("App.DotVersion", () => app.DotVersion, v => app.DotVersion = v),
            new("App.Version",    () => app.Version,    v => app.Version    = v),
        };

        // Table rows: the grids on this view hold free text too, so a
        // placeholder typed into a cell must expand like any field.
        var index = 0;
        foreach (var entry in app.DetectRunning)
        {
            var d = entry; // capture per iteration
            var prefix = $"DetectRunning[{index++}]";
            fields.Add(new($"{prefix}.DisplayName", () => d.DisplayName, v => d.DisplayName = v));
            fields.Add(new($"{prefix}.ExeFileName", () => d.ExeFileName, v => d.ExeFileName = v));
            fields.Add(new($"{prefix}.Process",     () => d.Process,     v => d.Process     = v));
        }

        // Plain string collection: index-addressed so the setter writes back
        // into the collection slot (which raises CollectionChanged).
        AddStringListFields(fields, app.Dependencies, "App.Dependencies");

        return fields;
    }

    /// <summary>
    /// Adds accessors for an <c>ObservableCollection&lt;string&gt;</c>, whose
    /// items have no property to set - the setter replaces the slot by index.
    /// </summary>
    private static void AddStringListFields(
        List<PlaceholderFieldAccessor> fields,
        System.Collections.ObjectModel.ObservableCollection<string> list,
        string label)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var slot = i; // capture per iteration
            fields.Add(new($"{label}[{slot}]",
                () => slot < list.Count ? list[slot] : string.Empty,
                v => { if (slot < list.Count) list[slot] = v; }));
        }
    }

    /// <summary>
    /// Intune scope: the selected package's free-text string fields plus its
    /// assignments' free-text fields. Ids, enums, times-as-enums (intent,
    /// notification, ...) and icon paths are skipped.
    /// </summary>
    public static IReadOnlyList<PlaceholderFieldAccessor> IntunePackageFields(IntunePackageEntry pkg)
    {
        var fields = new List<PlaceholderFieldAccessor>
        {
            new("Package.AppName",          () => pkg.AppName,          v => pkg.AppName          = v),
            new("Package.Comment",          () => pkg.Comment,          v => pkg.Comment          = v),
            new("Package.Notes",            () => pkg.Notes,            v => pkg.Notes            = v),
            new("Package.InstallCommand",   () => pkg.InstallCommand,   v => pkg.InstallCommand   = v),
            new("Package.UninstallCommand", () => pkg.UninstallCommand, v => pkg.UninstallCommand = v),
            new("Package.Developer",        () => pkg.Developer,        v => pkg.Developer        = v),
            new("Package.Owner",            () => pkg.Owner,            v => pkg.Owner            = v),
            new("Package.InformationURL",   () => pkg.InformationURL,   v => pkg.InformationURL   = v),
            new("Package.PrivacyURL",       () => pkg.PrivacyURL,       v => pkg.PrivacyURL       = v),
            new("Package.PackageOption",    () => pkg.PackageOption,    v => pkg.PackageOption    = v),
            new("Package.ExistingAppID",    () => pkg.ExistingAppID,    v => pkg.ExistingAppID    = v),
        };

        var index = 0;
        foreach (var assignment in pkg.Assignments)
        {
            var a = assignment; // capture per iteration
            var prefix = $"Assignment[{index++}]";
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.AppName",      () => a.AppName,      v => a.AppName      = v));
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.GroupID",      () => a.GroupID,      v => a.GroupID      = v));
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.FilterName",   () => a.FilterName,   v => a.FilterName   = v));
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.AvailableTime",() => a.AvailableTime,v => a.AvailableTime= v));
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.DeadlineTime", () => a.DeadlineTime, v => a.DeadlineTime = v));
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.Label",        () => a.Label,        v => a.Label        = v));
        }

        // Table rows on the Intune view. Return codes are skipped: the code
        // itself is an int and Type is an enum-like selector.
        var ci = 0;
        foreach (var cat in pkg.Categories)
        {
            var c = cat;
            fields.Add(new($"Category[{ci++}].Name", () => c.Name, v => c.Name = v));
        }
        var si = 0;
        foreach (var tag in pkg.ScopeTags)
        {
            var t = tag;
            fields.Add(new($"ScopeTag[{si++}].Name", () => t.Name, v => t.Name = v));
        }
        var di = 0;
        foreach (var dep in pkg.Dependencies)
        {
            var d = dep;
            fields.Add(new($"Dependency[{di++}].AppName", () => d.AppName, v => d.AppName = v));
        }
        var pi = 0;
        foreach (var sup in pkg.Supersedence)
        {
            var s = sup;
            fields.Add(new($"Supersedence[{pi++}].AppName", () => s.AppName, v => s.AppName = v));
        }

        return fields;
    }

    /// <summary>
    /// SCCM scope: the selected package's free-text string fields plus its
    /// deployments' free-text fields. Ids, site codes, enum-like modes and
    /// icon paths are skipped.
    /// </summary>
    public static IReadOnlyList<PlaceholderFieldAccessor> SccmPackageFields(SCCMPackageEntry pkg)
    {
        var fields = new List<PlaceholderFieldAccessor>
        {
            new("Package.AppName",              () => pkg.AppName,              v => pkg.AppName              = v),
            new("Package.AppComment",           () => pkg.AppComment,           v => pkg.AppComment           = v),
            new("Package.Publisher",            () => pkg.Publisher,            v => pkg.Publisher            = v),
            new("Package.SoftwareVersion",      () => pkg.SoftwareVersion,      v => pkg.SoftwareVersion      = v),
            new("Package.Owner",                () => pkg.Owner,                v => pkg.Owner                = v),
            new("Package.SupportContact",       () => pkg.SupportContact,       v => pkg.SupportContact       = v),
            new("Package.Description",          () => pkg.Description,          v => pkg.Description          = v),
            new("Package.ReleaseDate",          () => pkg.ReleaseDate,          v => pkg.ReleaseDate          = v),
            new("Package.LocalizedName",        () => pkg.LocalizedName,        v => pkg.LocalizedName        = v),
            new("Package.LocalizedDescription", () => pkg.LocalizedDescription, v => pkg.LocalizedDescription = v),
            new("Package.Keywords",             () => pkg.Keywords,             v => pkg.Keywords             = v),
            new("Package.PrivacyUrl",           () => pkg.PrivacyUrl,           v => pkg.PrivacyUrl           = v),
            new("Package.UserDocumentation",    () => pkg.UserDocumentation,    v => pkg.UserDocumentation    = v),
            new("Package.LinkText",             () => pkg.LinkText,             v => pkg.LinkText             = v),
            new("Package.Name",                 () => pkg.Name,                 v => pkg.Name                 = v),
            new("Package.Comment",              () => pkg.Comment,              v => pkg.Comment              = v),
            new("Package.PackageOption",        () => pkg.PackageOption,        v => pkg.PackageOption        = v),
            new("Package.InstallCommand",       () => pkg.InstallCommand,       v => pkg.InstallCommand       = v),
            new("Package.UninstallCommand",     () => pkg.UninstallCommand,     v => pkg.UninstallCommand     = v),
            new("Package.RepairCommand",        () => pkg.RepairCommand,        v => pkg.RepairCommand        = v),
        };

        var index = 0;
        foreach (var deployment in pkg.Deployments)
        {
            var d = deployment; // capture per iteration
            var prefix = $"Deployment[{index++}]";
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.AppName",    () => d.AppName,    v => d.AppName    = v));
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.Collection", () => d.Collection, v => d.Collection = v));
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.Label",      () => d.Label,      v => d.Label      = v));
            fields.Add(new PlaceholderFieldAccessor($"{prefix}.Comment",    () => d.Comment,    v => d.Comment    = v));
        }

        // Table rows on the SCCM view.
        var bi = 0;
        foreach (var behavior in pkg.InstallBehaviors)
        {
            var b = behavior;
            var prefix = $"InstallBehavior[{bi++}]";
            fields.Add(new($"{prefix}.ExeFileName", () => b.ExeFileName, v => b.ExeFileName = v));
            fields.Add(new($"{prefix}.DisplayName", () => b.DisplayName, v => b.DisplayName = v));
        }
        var di = 0;
        foreach (var dep in pkg.Dependencies)
        {
            var d2 = dep;
            fields.Add(new($"Dependency[{di++}].AppName", () => d2.AppName, v => d2.AppName = v));
        }
        var pi = 0;
        foreach (var sup in pkg.Supersedence)
        {
            var s = sup;
            fields.Add(new($"Supersedence[{pi++}].AppName", () => s.AppName, v => s.AppName = v));
        }

        return fields;
    }
}
