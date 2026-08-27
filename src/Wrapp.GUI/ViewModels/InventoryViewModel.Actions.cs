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

/// <summary>
/// User-action commands for <see cref="InventoryViewModel"/>: clipboard
/// copy of detail sections, JSON export, intunewin download, icon save,
/// and the "Import to Wrapp" workflow with its config-mapping helpers.
/// Moved into a partial file so the core VM focuses on list/detail
/// navigation + filtering, and these per-app actions are colocated.
/// </summary>
public partial class InventoryViewModel
{
    // -----------------------------------------------------------------------
    // Actions
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void CopySection(string? section)
    {
        if (AppDetail is null || string.IsNullOrEmpty(section)) return;
        if (!_sectionBuilders.TryGetValue(section, out var builder)) return;
        var body = builder(AppDetail);
        if (string.IsNullOrWhiteSpace(body)) return;

        System.Windows.Clipboard.SetText(body.TrimEnd());
        StatusText = $"{section} copied to clipboard.";
    }

    [RelayCommand]
    private void CopyAllDetails()
    {
        if (AppDetail is null) return;
        var d = AppDetail;
        var sb = new System.Text.StringBuilder();

        void Append(string title, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"=== {title} ===");
            sb.AppendLine(body);
        }

        // Section order matches the detail pane, top-to-bottom, and each
        // section body comes from the same builder CopySection uses so
        // what you copy per-section is identical to the corresponding
        // block inside the Copy All output.
        Append($"App Information ({d.Platform})", BuildAppInfoBody(d));
        Append("Program", BuildProgramBody(d));
        if (d.Platform == AppPlatform.Intune)
            Append("Requirements", BuildRequirementsBody(d));
        Append("Detection Rules", BuildDetectionBody(d));
        if (d.Platform == AppPlatform.Intune)
            Append("Scope Tags", BuildScopeTagsBody(d));
        Append($"Assignments ({d.Assignments.Count})", BuildAssignmentsBody(d));
        if (d.Dependencies.Count > 0)
            Append($"Dependencies ({d.Dependencies.Count})", BuildDependenciesBody(d));
        if (d.DependedOnBy.Count > 0)
            Append($"Depended On By ({d.DependedOnBy.Count})", BuildDependedOnByBody(d));
        if (d.Supersedence.Count > 0)
            Append($"Supersedence ({d.Supersedence.Count})", BuildSupersedenceBody(d));
        if (d.SupersededBy.Count > 0)
            Append($"Superseded By ({d.SupersededBy.Count})", BuildSupersededByBody(d));
        if (d.Platform == AppPlatform.Intune && d.ReturnCodes.Count > 0)
            Append($"Return Codes ({d.ReturnCodes.Count})", BuildReturnCodesBody(d));

        System.Windows.Clipboard.SetText(sb.ToString().TrimEnd());
        StatusText = "All details copied to clipboard.";
    }

    // ── Per-section shape definitions ───────────────────────────────────
    //
    // Each section has exactly one `Build<section>Body(detail)` method
    // that returns the plain-text body (no "===" title bar). Both
    // CopySection (single section) and CopyAllDetails (every section
    // concatenated with title bars) call these same builders, so
    // per-section copy output and the bottom full-export are guaranteed
    // to match field-for-field. To change what a section emits, change
    // exactly one builder.


    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (AppDetail is null) return;

        // Wrapp's intelligence perk: the export can carry the full nested-
        // group expansion (which the Intune UI doesn't offer). Offered as an
        // opt-in checkbox only when this app actually HAS resolved nested
        // groups; otherwise the export is exactly the classic detail JSON.
        var includeNested = false;
        if (AppDetail.Assignments.Any(a => a.NestedGroups is not null))
        {
            var check = new System.Windows.Controls.CheckBox
            {
                Content = "Include nested group expansion (full member-group trees per assignment)",
                IsChecked = true,
            };
            var panel = new System.Windows.Controls.StackPanel { MaxWidth = 460 };
            panel.Children.Add(check);
            if (!await FluentDialog.ShowSelectAsync(
                    $"Export {AppDetail.DisplayName}", panel, "Export", "Cancel"))
                return;
            includeNested = check.IsChecked == true;
        }

        if (!includeNested)
        {
            await ExportToJsonFileAsync(AppDetail, AppDetail.DisplayName, "Export App Detail");
            return;
        }

        // Same detail JSON, with one appended "NestedGroups" property — the
        // existing shape stays byte-compatible for consumers that ignore it.
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(AppDetail, JsonDefaults.Pretty))!.AsObject();
        node["NestedGroups"] = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(
                BuildNestedGroupsSection(AppDetail), JsonDefaults.Pretty));
        await ExportToJsonFileAsync(node, AppDetail.DisplayName, "Export App Detail");
    }

    /// <summary>The per-assignment nested-group section appended to exports.
    /// Internal + static so the catalog export reuses the identical shape.</summary>
    internal static List<object> BuildNestedGroupsSection(AppInventoryDetail detail)
        => detail.Assignments
            .Where(a => a.NestedGroups is not null)
            .Select(a => (object)new
            {
                a.GroupId,
                GroupName = a.TargetLabel,
                a.Intent,
                a.NestedGroups!.RootDisplayName,
                a.NestedGroups.MaxDepth,
                a.NestedGroups.HasCircularReference,
                TotalNestedGroups = a.NestedGroups.AllNestedGroupNames.Count,
                Groups = BuildNestedGroupExport(a.NestedGroups.TreeRoot),
            })
            .ToList();

    [RelayCommand]
    private async Task ExportAssignmentsJsonAsync()
    {
        if (AppDetail is null) return;

        // Build a rich assignment export with nested group details
        var export = new
        {
            AppName = AppDetail.DisplayName,
            AppId = AppDetail.Id,
            ExportedAt = SystemClock.UtcNow.ToString("o"),
            AssignmentCount = AppDetail.Assignments.Count,
            Assignments = AppDetail.Assignments.Select(a => new
            {
                a.Intent,
                a.TargetType,
                a.GroupId,
                GroupName = a.TargetLabel,
                a.GroupMode,
                a.Source,
                a.Notification,
                a.AvailableTime,
                a.DeadlineTime,
                a.DeliveryOptimization,
                a.RestartGracePeriod,
                a.FilterId,
                a.FilterMode,
                NestedGroups = a.NestedGroups is not null ? new
                {
                    a.NestedGroups.RootGroupId,
                    a.NestedGroups.RootDisplayName,
                    a.NestedGroups.MaxDepth,
                    a.NestedGroups.HasCircularReference,
                    TotalNestedGroups = a.NestedGroups.AllNestedGroupNames.Count,
                    Groups = BuildNestedGroupExport(a.NestedGroups.TreeRoot)
                } : null
            }).ToList()
        };

        await ExportToJsonFileAsync(export,
            $"{AppDetail.DisplayName} - Assignments", "Export Assignments");
    }

    internal static List<object> BuildNestedGroupExport(NestedGroupNode? node)
    {
        if (node is null) return new List<object>();
        return node.Children.Select(c => (object)new
        {
            c.GroupId,
            c.DisplayName,
            c.Description,
            c.Mail,
            c.GroupType,
            c.Visibility,
            c.SecurityEnabled,
            c.CreatedDateTime,
            c.IsCircular,
            c.MemberCount,
            Children = c.Children.Count > 0 ? BuildNestedGroupExport(c) : null
        }).ToList();
    }

    private static async Task ExportToJsonFileAsync(object data, string defaultName, string title)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(data, JsonDefaults.Pretty);

        var safeName = FileNameSanitizer.Sanitize(defaultName);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{safeName}.json",
            Filter = "JSON files (*.json)|*.json",
            Title = title
        };

        if (dialog.ShowDialog() == true)
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, json);
            AppLogger.Info($"Inventory: exported '{defaultName}' to {dialog.FileName}");
            await FluentDialog.ShowExportedAsync("Export complete",
                $"\"{defaultName}\" was exported.", dialog.FileName);
        }
    }

    [RelayCommand]
    private async Task DownloadIntuneWinAsync()
    {
        if (AppDetail is null || SelectedTarget is null) return;

        var safeName = FileNameSanitizer.Sanitize(AppDetail.DisplayName);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{safeName}.intunewin",
            Filter = "IntuneWin files (*.intunewin)|*.intunewin|All files (*.*)|*.*",
            Title = "Download .intunewin Content"
        };
        if (dialog.ShowDialog() != true) return;

        IsBackgroundWorking = true;
        StatusText = $"Downloading content for '{AppDetail.DisplayName}'...";
        var job = _jobTracker?.BeginJob($"Downloading {safeName}.intunewin") ?? default;

        try
        {
            var result = await _inventoryService.DownloadRawContentAsync(
                SelectedTarget.Key, AppDetail.Id, dialog.FileName,
                UiProgress.ForStatus(msg =>
                {
                    StatusText = msg;
                    job.SetStatus(msg);
                }));

            if (result)
            {
                StatusText = $"Downloaded to {dialog.FileName}";
                job.Complete($"Downloaded {safeName}.intunewin");
                await FluentDialog.ShowExportedAsync("Download complete",
                    $"The raw .intunewin content for \"{AppDetail.DisplayName}\" was downloaded (still Intune-encrypted; decrypt via Tools).",
                    dialog.FileName);
            }
            else
            {
                // StatusText was already set by the progress callback with a specific reason
                if (string.IsNullOrEmpty(StatusText) || !StatusText.StartsWith("Download failed"))
                    StatusText = "Download failed -- no content returned.";
                job.Fail(StatusText);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Download failed: {ex.Message}";
            job.Fail(ex.Message);
            AppLogger.Warn($"Inventory: .intunewin download failed: {ex.Message}");
        }
        finally
        {
            IsBackgroundWorking = false;
        }
    }

    [RelayCommand]
    private async Task SaveIconAsync()
    {
        if (AppDetail is null || SelectedTarget is null) return;

        // Lazy-load icon if not yet fetched (largeIcon is a heavy property
        // not included in the standard $expand query)
        if (string.IsNullOrEmpty(AppDetail.IconBase64))
        {
            StatusText = "Fetching icon...";
            var iconData = await _inventoryService.FetchIconBase64Async(SelectedTarget.Key, AppDetail.Id);
            if (string.IsNullOrEmpty(iconData))
            {
                StatusText = "No icon available for this app.";
                return;
            }
            AppDetail.IconBase64 = iconData;
        }

        var safeName = FileNameSanitizer.Sanitize(AppDetail.DisplayName);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{safeName}.png",
            Filter = "PNG images (*.png)|*.png|All files (*.*)|*.*",
            Title = "Save App Icon"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var bytes = Convert.FromBase64String(AppDetail.IconBase64);
            await System.IO.File.WriteAllBytesAsync(dialog.FileName, bytes);
            StatusText = $"Icon saved to {dialog.FileName}";
            AppLogger.Info($"Inventory: saved icon to {dialog.FileName}");
            await FluentDialog.ShowExportedAsync("Icon saved",
                $"The app icon for \"{AppDetail.DisplayName}\" was saved.", dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText = $"Icon save failed: {ex.Message}";
        }
    }


    [RelayCommand]
    private async Task ShowNestedGroups(InventoryAssignmentInfo? assignment)
    {
        if (assignment?.NestedGroups is null) return;
        var query = SearchText?.Trim() ?? "";
        var dialog = new Views.NestedGroupBrowserDialog(assignment.NestedGroups, query);
        await FluentDialog.ShowContentAsync(
            $"Nested Groups - {assignment.TargetLabel}", dialog);
    }

}