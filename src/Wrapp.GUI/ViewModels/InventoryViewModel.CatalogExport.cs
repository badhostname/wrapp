using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.Input;
using CheckBox = System.Windows.Controls.CheckBox;       // WinForms also declares these
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.ViewModels;

/// <summary>
/// Catalog-wide export for an Intune tenant: everything the per-app export
/// offers, for EVERY app in the queried catalog, into a tenant-rooted
/// directory tree. Options mirror the template-save checkbox style; the run
/// is a background job with live counters, and completion lands in the
/// unified exported-path prompt.
///
/// Layout:
///   &lt;chosen&gt;\&lt;tenant&gt;\catalog.json          — flat list (Name/Id/Version), no nested detail
///   &lt;chosen&gt;\&lt;tenant&gt;\&lt;app&gt;\app.json        — the full detail JSON (same shape as per-app
///                                              export; nested-group section appended on opt-in)
///   &lt;chosen&gt;\&lt;tenant&gt;\&lt;app&gt;\icon.png        — optional
///   &lt;chosen&gt;\&lt;tenant&gt;\&lt;app&gt;\&lt;app&gt;.intunewin — optional (raw, still Intune-encrypted)
/// </summary>
public partial class InventoryViewModel
{
    /// <summary>Greyed until the query has loaded AND the enrichment jobs
    /// (detail preload, group names, nested groups) have completed — the
    /// export depends on the fully enriched catalog. IsBackgroundWorking is
    /// the jobs' completion flag; the command re-arms via
    /// NotifyCanExecuteChangedFor on both fields.</summary>
    private bool CanExportCatalog()
        => Platform == AppPlatform.Intune
           && SelectedTarget is not null
           && HasLoadedData
           && !IsBackgroundWorking;

    [RelayCommand(CanExecute = nameof(CanExportCatalog))]
    private async Task ExportCatalogAsync()
    {
        if (!CanExportCatalog()) return; // belt for programmatic invocation

        var tenantKey = SelectedTarget.Key;
        var tenantLabel = SelectedTarget.Display;
        var apps = _inventoryService.GetCachedIntuneApps(tenantKey);
        if (apps is null || apps.Count == 0)
        {
            await FluentDialog.ShowInfoAsync("Catalog export", "No cached catalog for this tenant — run Query first.");
            return;
        }

        // ---- Options (same look as the template-save field picker:
        //      SemiBold name + secondary detail, rows inside a CardBg border
        //      so the checkbox outlines read against the dialog surface) ----
        var anyNested = apps.Any(a => a.HasNestedGroupData);

        static CheckBox MakeOption(string name, string detail, bool isChecked = false, bool isEnabled = true)
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
            label.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(detail))
            {
                var sep = new Run("  —  ");
                sep.SetResourceReference(TextElement.ForegroundProperty, "TextMutedBrush");
                label.Inlines.Add(sep);
                var det = new Run(detail);
                det.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
                label.Inlines.Add(det);
            }
            return new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                IsEnabled = isEnabled,
                Margin = new Thickness(0, 3, 0, 3),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
        }

        var fullJson = MakeOption("Full detail JSON per app", $"{apps.Count} apps", isChecked: true);
        var nested = MakeOption("Nested group expansion",
            anyNested ? "where detected" : "none detected in this catalog",
            isChecked: anyNested, isEnabled: anyNested);
        var icons = MakeOption("App icons", "icon.png — one Graph call per app");
        var intunewin = MakeOption("Raw .intunewin content", "large downloads; still Intune-encrypted");

        var rows = new StackPanel();
        foreach (var cb in new[] { fullJson, nested, icons, intunewin })
            rows.Children.Add(cb);

        // Same three-part structure as SaveTemplateWindow's field picker:
        // muted hint, a section caption, then a bordered CardBg panel whose
        // gently-scrolling viewport holds the rows.
        var hint = new TextBlock
        {
            Text = "Exports into a folder named for the tenant: catalog.json (name/id/version index) at the root, then one directory per app holding everything checked below.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var caption = new TextBlock
        {
            Text = "Content to include",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var scroll = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 4, 12, 4),
        };
        SmoothScroll.SetEnabled(scroll, true);

        var rowsBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = scroll,
        };
        rowsBorder.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
        rowsBorder.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");

        var panel = new StackPanel { MaxWidth = 480 };
        panel.Children.Add(hint);
        panel.Children.Add(caption);
        panel.Children.Add(rowsBorder);

        if (!await FluentDialog.ShowSelectAsync($"Export catalog — {tenantLabel}", panel, "Export", "Cancel"))
            return;
        var options = (Json: fullJson.IsChecked == true, Nested: nested.IsChecked == true,
                       Icons: icons.IsChecked == true, IntuneWin: intunewin.IsChecked == true);
        if (options is { Json: false, Icons: false, IntuneWin: false })
        {
            await FluentDialog.ShowInfoAsync("Catalog export", "Nothing selected to export.");
            return;
        }

        var chosenRoot = FileDialogService.BrowseFolder("Select the export destination folder");
        if (string.IsNullOrEmpty(chosenRoot)) return;

        var tenantRoot = Path.Combine(chosenRoot, FileNameSanitizer.Sanitize(tenantLabel));

        // ---- The job ----
        var job = _jobTracker?.BeginJob($"Catalog export: {tenantLabel}") ?? default;
        job.SetDetail("Tenant", tenantLabel);
        job.SetDetail("Apps", apps.Count.ToString());
        job.SetDetail("Destination", tenantRoot);
        IsBackgroundWorking = true;
        StatusText = $"Exporting catalog ({apps.Count} apps)...";
        var dispatcher = System.Windows.Application.Current.Dispatcher;

        _ = Task.Run(async () =>
        {
            int jsonOk = 0, nestedOk = 0, iconOk = 0, winOk = 0, failures = 0;
            var errors = new List<string>();
            try
            {
                Directory.CreateDirectory(tenantRoot);

                // Tenant-root catalog: names only, no nested detail.
                var catalog = apps
                    .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(a => new { Name = a.DisplayName, a.Id, Version = a.AppVersion })
                    .ToList();
                await File.WriteAllTextAsync(
                    Path.Combine(tenantRoot, "catalog.json"),
                    JsonSerializer.Serialize(new
                    {
                        Tenant = tenantLabel,
                        TenantKey = tenantKey,
                        ExportedAt = SystemClock.UtcNow.ToString("o"),
                        AppCount = apps.Count,
                        Apps = catalog,
                    }, JsonDefaults.Pretty));

                var usedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < apps.Count; i++)
                {
                    var app = apps[i];
                    job.SetStatus($"{app.DisplayName} ({i + 1}/{apps.Count})");
                    job.SetProgress((int)((i + 1) * 100.0 / apps.Count));

                    try
                    {
                        // Duplicate display names get a stable id suffix.
                        var dirName = FileNameSanitizer.Sanitize(app.DisplayName);
                        if (!usedDirs.Add(dirName))
                        {
                            dirName = $"{dirName}_{app.Id[..Math.Min(8, app.Id.Length)]}";
                            usedDirs.Add(dirName);
                        }
                        var appDir = Path.Combine(tenantRoot, dirName);
                        Directory.CreateDirectory(appDir);

                        // Detail through the load path (NOT the raw cache):
                        // it back-fills dependency/supersedence relationships
                        // the $batch preload leaves empty.
                        var detail = await _inventoryService.GetIntuneAppDetailAsync(tenantKey, app.Id);
                        if (detail is null) { failures++; errors.Add($"{app.DisplayName}: no detail"); continue; }

                        if (options.Json)
                        {
                            var node = JsonNode.Parse(
                                JsonSerializer.Serialize(detail, JsonDefaults.Pretty))!.AsObject();
                            if (options.Nested && detail.Assignments.Any(a => a.NestedGroups is not null))
                            {
                                node["NestedGroups"] = JsonNode.Parse(JsonSerializer.Serialize(
                                    BuildNestedGroupsSection(detail), JsonDefaults.Pretty));
                                nestedOk++;
                            }
                            await File.WriteAllTextAsync(
                                Path.Combine(appDir, "app.json"),
                                node.ToJsonString(JsonDefaults.Pretty));
                            jsonOk++;
                        }

                        if (options.Icons)
                        {
                            var b64 = detail.IconBase64;
                            if (string.IsNullOrEmpty(b64))
                            {
                                b64 = await _inventoryService.FetchIconBase64Async(tenantKey, app.Id);
                                if (!string.IsNullOrEmpty(b64)) detail.IconBase64 = b64;
                            }
                            if (!string.IsNullOrEmpty(b64))
                            {
                                await File.WriteAllBytesAsync(
                                    Path.Combine(appDir, "icon.png"), Convert.FromBase64String(b64));
                                iconOk++;
                            }
                        }

                        if (options.IntuneWin)
                        {
                            var winPath = Path.Combine(appDir, $"{dirName}.intunewin");
                            if (await _inventoryService.DownloadRawContentAsync(
                                    tenantKey, app.Id, winPath, progress: null))
                                winOk++;
                            else
                                errors.Add($"{app.DisplayName}: content unavailable");
                        }

                        job.SetDetail("Exported", $"{jsonOk} json, {nestedOk} nested, {iconOk} icons, {winOk} intunewin");
                    }
                    catch (Exception exApp)
                    {
                        failures++;
                        errors.Add($"{app.DisplayName}: {exApp.Message}");
                        AppLogger.Warn($"Catalog export: '{app.DisplayName}' failed — {exApp.Message}");
                    }
                }

                if (errors.Count > 0)
                {
                    job.SetDetail("Issues", errors.Count.ToString());
                    job.SetError("PartialExport", string.Join(Environment.NewLine, errors));
                }

                await dispatcher.InvokeAsync(async () =>
                {
                    IsBackgroundWorking = false;
                    StatusText = $"Catalog exported: {apps.Count} app(s) -> {tenantRoot}";
                    job.Complete($"{apps.Count} app(s) exported ({errors.Count} issue(s))");
                    await FluentDialog.ShowExportedAsync(
                        "Catalog exported",
                        $"{tenantLabel}: {jsonOk} app JSON file(s)"
                        + (nestedOk > 0 ? $", {nestedOk} with nested groups" : "")
                        + (options.Icons ? $", {iconOk} icon(s)" : "")
                        + (options.IntuneWin ? $", {winOk} .intunewin package(s)" : "")
                        + (errors.Count > 0 ? $" — {errors.Count} issue(s), see the job's details." : "."),
                        tenantRoot);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Catalog export failed — {ex.Message}");
                job.SetError(ex.GetType().Name, ex.ToString());
                await dispatcher.InvokeAsync(() =>
                {
                    IsBackgroundWorking = false;
                    StatusText = $"Catalog export failed: {ex.Message}";
                    job.Fail(ex.Message);
                });
            }
        });
    }
}
