using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

public partial class InventoryViewModel
{
    // -----------------------------------------------------------------------
    // Clipboard-section formatters. The 11 Build*Body methods are pure
    // string formatters; the inner ClipboardSection helper class composes
    // them into a tab-separated 'flat' export the system clipboard expects.
    // The dispatch dictionary at the top wires the section keys used by
    // the CopySection command back to the appropriate formatter.
    // -----------------------------------------------------------------------

    private static readonly Dictionary<string, Func<AppInventoryDetail, string>> _sectionBuilders = new()
    {
        ["AppInfo"]      = BuildAppInfoBody,
        ["Program"]      = BuildProgramBody,
        ["Requirements"] = BuildRequirementsBody,
        ["Detection"]    = BuildDetectionBody,
        ["ScopeTags"]    = BuildScopeTagsBody,
        ["Assignments"]  = BuildAssignmentsBody,
        ["Dependencies"] = BuildDependenciesBody,
        ["DependedOnBy"] = BuildDependedOnByBody,
        ["Supersedence"] = BuildSupersedenceBody,
        ["SupersededBy"] = BuildSupersededByBody,
        ["ReturnCodes"]  = BuildReturnCodesBody,
    };

    // Build* helpers + ClipboardSection are `internal` (not `private`)
    // so the test assembly can exercise the clipboard shape directly
    // via InternalsVisibleTo. See Wrapp.GUI.csproj.
    internal static string BuildAppInfoBody(AppInventoryDetail d)
    {
        if (d.Platform == AppPlatform.Intune)
            return ClipboardSection.Flat(
                ("Name",         d.DisplayName),
                ("ID",           d.Id),
                ("Description",  d.Description),
                ("Publisher",    d.Publisher),
                ("Version",      d.Version),
                ("Category",     d.CategoriesDisplay == "-" ? null : d.CategoriesDisplay),
                ("Featured",     d.IsFeatured),
                ("Developer",    d.Developer),
                ("Owner",        d.Owner),
                ("Info URL",     d.InformationUrl),
                ("Privacy URL",  d.PrivacyUrl),
                ("Notes",        d.Notes),
                ("Created",      d.CreatedDateTime),
                ("Modified",     d.LastModifiedDateTime));

        // SCCM
        return ClipboardSection.Flat(
            ("Name",             d.DisplayName),
            ("ID",               d.Id),
            ("Description",      d.Description),
            ("Publisher",        d.Publisher),
            ("Version",          d.Version),
            ("Category",         d.CategoriesDisplay == "-" ? null : d.CategoriesDisplay),
            ("Technology",       d.Technology),
            ("Deployment Type",  d.DeploymentTypeName),
            ("Status",           d.IsEnabled ? "Enabled" : "Disabled"),
            ("Expired",          d.IsExpired ? (object?)true : null),
            ("Superseded",       d.IsSuperseded ? (object?)true : null),
            ("Console Folder",   d.ObjectPath),
            ("Deployment Types", d.NumberOfDeploymentTypes),
            ("Created",          d.CreatedDateTime),
            ("Modified",         d.LastModifiedDateTime),
            ("Created By",       d.CreatedBy),
            ("Modified By",      d.LastModifiedBy));
    }

    internal static string BuildProgramBody(AppInventoryDetail d) =>
        ClipboardSection.Flat(
            ("Install",           d.InstallCommand),
            ("Uninstall",         d.UninstallCommand),
            ("Content Location",  d.Platform == AppPlatform.SCCM   ? d.ContentLocation : null),
            ("Context",           d.InstallExperience),
            ("Restart",           d.Platform == AppPlatform.Intune ? d.RestartBehavior : null),
            ("Max time",          d.MaxInstallTime > 0   ? $"{d.MaxInstallTime} min" : null),
            ("File",              d.Platform == AppPlatform.Intune ? d.FileName : null),
            ("Size",              d.SizeDisplay));

    internal static string BuildRequirementsBody(AppInventoryDetail d)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ClipboardSection.Flat(
            ("Architecture",        d.Architecture),
            ("Min OS",              d.MinimumOSVersion),
            ("Disk space",          d.MinimumFreeDiskSpaceMB > 0 ? $"{d.MinimumFreeDiskSpaceMB} MB" : null),
            ("Memory",              d.MinimumMemoryMB > 0         ? $"{d.MinimumMemoryMB} MB"       : null),
            ("Processors",          d.MinimumProcessors > 0       ? (object?)d.MinimumProcessors    : null),
            ("CPU speed",           d.MinimumCpuSpeedMHz > 0      ? $"{d.MinimumCpuSpeedMHz} MHz"   : null)));
        foreach (var r in d.Requirements)
            sb.AppendLine($"[{r.RuleType}] {r.Summary}");
        return sb.ToString().TrimEnd();
    }

    internal static string BuildDetectionBody(AppInventoryDetail d)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ClipboardSection.Flat(
            ("Type",    d.DetectionType),
            ("Summary", d.DetectionSummary)));
        if (d.HasDetectionScript)
        {
            sb.AppendLine("Script:");
            sb.AppendLine(d.DetectionScript);
        }
        return sb.ToString().TrimEnd();
    }

    internal static string BuildScopeTagsBody(AppInventoryDetail d) =>
        d.ScopeTagsDisplay;

    internal static string BuildAssignmentsBody(AppInventoryDetail d) =>
        ClipboardSection.ListOfObjects(d.AssignmentsSorted, a => new (string, object?)[]
        {
            ("Group Mode",     a.GroupMode),
            ("Intent",         a.Intent),
            ("Target",         a.TargetLabel),
            ("Group ID",       a.GroupId),
            ("Filter",         string.IsNullOrEmpty(a.FilterId) ? null : $"{a.FilterMode} ({a.FilterId})"),
            ("Notification",   a.Notification),
            ("Availability",   a.AvailableTime),
            ("Deadline",       a.DeadlineTime),
            ("Restart Grace",  a.RestartGracePeriod),
            ("Delivery Opt",   a.DeliveryOptimization),
            ("Source",         a.Source),
        });

    internal static string BuildDependenciesBody(AppInventoryDetail d) =>
        ClipboardSection.ListOfObjects(d.Dependencies, dep => new (string, object?)[]
        {
            ("App Name",     dep.AppName),
            ("App ID",       dep.AppId),
            ("Type",         dep.Type),
            ("Auto Install", dep.AutoInstallLabel),
        });

    internal static string BuildDependedOnByBody(AppInventoryDetail d) =>
        ClipboardSection.ListOfObjects(d.DependedOnBy, dep => new (string, object?)[]
        {
            ("App Name",     dep.AppName),
            ("App ID",       dep.AppId),
            ("Type",         dep.Type),
            ("Auto Install", dep.AutoInstallLabel),
        });

    internal static string BuildSupersedenceBody(AppInventoryDetail d) =>
        ClipboardSection.ListOfObjects(d.Supersedence, sup => new (string, object?)[]
        {
            ("App Name",          sup.AppName),
            ("App ID",            sup.AppId),
            ("Type",              sup.Type),
            ("Uninstall Previous", sup.UninstallLabel),
        });

    internal static string BuildSupersededByBody(AppInventoryDetail d) =>
        ClipboardSection.ListOfObjects(d.SupersededBy, sup => new (string, object?)[]
        {
            ("App Name",          sup.AppName),
            ("App ID",            sup.AppId),
            ("Type",              sup.Type),
            ("Uninstall Previous", sup.UninstallLabel),
        });

    internal static string BuildReturnCodesBody(AppInventoryDetail d)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var rc in d.ReturnCodes)
            sb.AppendLine($"{rc.Code} = {rc.Type}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Generic clipboard-section formatter: turns a list of (label, value)
    /// tuples into a consistent, whitespace-trimmed, empty-value-skipping
    /// block of text. Used by every BuildXxxBody method so per-section
    /// copy and the Copy-All export always agree on formatting.
    /// </summary>
    internal static class ClipboardSection
    {
        // Flat key/value section: "Label: Value" per non-empty field.
        public static string Flat(params (string Label, object? Value)[] fields)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (label, value) in fields)
            {
                var s = Format(value);
                if (string.IsNullOrEmpty(s)) continue;
                sb.AppendLine($"{label}: {s}");
            }
            return sb.ToString().TrimEnd();
        }

        // List of typed items, each rendered as its own "card" of
        // "  Label: Value" lines separated by a blank line. Returns
        // "None" when the list is empty (matches what the UI shows).
        public static string ListOfObjects<T>(
            IEnumerable<T> items,
            Func<T, (string Label, object? Value)[]> shape,
            string emptyText = "None")
        {
            var list = items.ToList();
            if (list.Count == 0) return emptyText;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.AppendLine();
                foreach (var (label, value) in shape(list[i]))
                {
                    var s = Format(value);
                    if (string.IsNullOrEmpty(s)) continue;
                    sb.AppendLine($"  {label}: {s}");
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static string Format(object? v) => v switch
        {
            null              => "",
            bool b            => b ? "Yes" : "No",
            int i             => i == 0 ? "" : i.ToString(),
            string s          => s ?? "",
            _                 => v.ToString() ?? "",
        };
    }
}
