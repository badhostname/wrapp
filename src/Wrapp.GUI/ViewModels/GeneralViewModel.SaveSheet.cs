using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Policy;

namespace Wrapp.ViewModels;

public partial class GeneralViewModel
{
    /// <summary>
    /// Shows the save sheet and returns the folder to save to, or null when
    /// the operator cancels. The chosen mode is remembered in Settings
    /// (unless policy fixes it) and the location is remembered for this
    /// computer, so the next sheet opens where this one ended.
    /// </summary>
    /// <param name="title">The dialog title: "Save bundle", "Save bundle as", "Save upgraded bundle as".</param>
    /// <param name="app">The app fields the naming convention applies to (the new ones, for upgrade and full apply).</param>
    /// <param name="mode">A mode to open in instead of the Settings one; the Upgrade flow opens in Direct.</param>
    /// <param name="location">A location to open at instead of the remembered one; the Upgrade flow opens at the current bundle's parent.</param>
    private async Task<string?> ShowSaveSheetAsync(string title, AppSection app, SaveTargetMode? mode = null, string? location = null)
    {
        var currentRoot = IsTempWorkspace() ? null : BundleRootDir;
        var target = SaveTarget.For(_settings, app, currentRoot);
        var modeManaged = PolicyService.Current.Mandatory.ContainsKey(nameof(AppSettings.BundleSaveMode));
        if (mode is not null && !modeManaged) target.Mode = mode.Value;
        if (!string.IsNullOrWhiteSpace(location))
        {
            target.Location = location;
            target.LocationSource = "the folder the current bundle is in";
        }
        target.Refresh(_settings, app, currentRoot);

        var sheet = new Views.SaveTargetDialog(target, _settings, app, currentRoot);
        while (true)
        {
            var ok = await FluentDialog.ShowSelectAsync(title, sheet, "Save", "Cancel");
            if (!ok)
            {
                AppLogger.Info($"{title}: cancelled at the save sheet");
                return null;
            }

            target.Refresh(_settings, app, currentRoot);
            if (target.Error is not null)
            {
                await FluentDialog.ShowWarningAsync("Cannot save there", target.Error);
                continue;
            }

            RememberSaveTarget(target, modeManaged);
            AppLogger.Info($"{title}: {target.Mode} -> {target.ResolvedPath}"
                           + (target.Warnings.Count > 0 ? $" (warnings: {string.Join(" | ", target.Warnings)})" : ""));
            return target.ResolvedPath;
        }
    }

    private void RememberSaveTarget(SaveTarget target, bool modeManaged)
    {
        var changed = false;
        if (!string.Equals(_settings.LastBundleOutputRoot, target.Location, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastBundleOutputRoot = target.Location;
            changed = true;
        }
        var modeSetting = SaveTarget.ModeSetting(target.Mode);
        if (!modeManaged && !string.Equals(_settings.BundleSaveMode, modeSetting, StringComparison.OrdinalIgnoreCase))
        {
            _settings.BundleSaveMode = modeSetting;
            changed = true;
        }
        if (!changed) return;
        try { SettingsService.Save(_settings); }
        catch (Exception ex) { AppLogger.Warn($"Save sheet: could not remember the location: {ex.Message}"); }
    }
}
