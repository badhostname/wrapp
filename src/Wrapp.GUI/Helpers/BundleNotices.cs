using System.IO;
using System.Text.RegularExpressions;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Helpers;

public enum BundleNoticeLevel { Info, Warning }

/// <summary>What the one-click fix on a notice does; <see cref="None"/> means the next save handles it.</summary>
public enum BundleNoticeFix { None, SetVersionFromFolder, CopyToConventionalFolder, OpenIntuneOutput }

/// <summary>One thing the open bundle's folder and its config disagree on.</summary>
public sealed class BundleNotice
{
    public required string Id { get; init; }
    public required BundleNoticeLevel Level { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public BundleNoticeFix Fix { get; init; } = BundleNoticeFix.None;
    public string FixLabel { get; init; } = string.Empty;
    /// <summary>What the fix needs: the folder's version for <see cref="BundleNoticeFix.SetVersionFromFolder"/>.</summary>
    public string? Data { get; init; }

    public bool IsWarning => Level == BundleNoticeLevel.Warning;
    public bool IsInfo => Level == BundleNoticeLevel.Info;
    public bool HasFix => Fix != BundleNoticeFix.None && FixLabel.Length > 0;
}

/// <summary>
/// Notices for the open bundle, computed from the real folder and the
/// config on open and after every save. Nothing is stored to produce or
/// dismiss them: a bundle moved in Explorer is simply looked at again. Pure
/// apart from probing the disk, so it runs off the UI thread.
/// </summary>
public static class BundleNotices
{
    private static readonly Regex VersionFolder = new(@"^\d+(?:[._]\d+)+$", RegexOptions.Compiled);

    /// <summary>"4_45_0_0" or "4.45.0.0" as "4.45.0.0"; null when the name is not a version.</summary>
    public static string? VersionFromFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        var name = folderName.Trim();
        return VersionFolder.IsMatch(name) ? name.Replace('_', '.') : null;
    }

    public static IReadOnlyList<BundleNotice> Evaluate(string? bundleRoot, string? configPath, AppConfigModel config, AppSettings settings)
    {
        var list = new List<BundleNotice>();
        if (string.IsNullOrWhiteSpace(bundleRoot)) return list;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        if (root.StartsWith(Path.TrimEndingDirectorySeparator(TempWorkspaceService.RootPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return list;

        var app = config.App;
        var folderName = Path.GetFileName(root);

        // 1. The folder is named after a version that is not the config's.
        if (VersionFromFolderName(folderName) is { } folderVersion)
        {
            var configDot = (app.DotVersion ?? string.Empty).Trim();
            var configUnderscore = (app.Version ?? string.Empty).Trim().Replace('_', '.');
            if (!string.Equals(folderVersion, configDot, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(folderVersion, configUnderscore, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new BundleNotice
                {
                    Id = "version-folder", Level = BundleNoticeLevel.Warning,
                    Title = $"The folder says {folderVersion}; the config says {(configDot.Length > 0 ? configDot : "nothing")}.",
                    Detail = $"The bundle is in '{folderName}' but App Info carries version {(configDot.Length > 0 ? configDot : "(empty)")}. Packaging uses the config; the folder name is what people see on the share.",
                    Fix = BundleNoticeFix.SetVersionFromFolder, FixLabel = "Set version from folder", Data = folderVersion,
                });
            }
        }

        // 2. The folder does not follow the naming convention. Only when the
        //    operator saves by the convention; in direct mode the tree is theirs.
        if (string.Equals(settings.BundleSaveMode, BundleService.SaveModeConvention, StringComparison.OrdinalIgnoreCase)
            && BundleService.TryResolveSubDirectory(settings, app, out var expected, out _)
            && !FollowsConvention(root, expected))
        {
            list.Add(new BundleNotice
            {
                Id = "convention", Level = BundleNoticeLevel.Info,
                Title = "The folder does not follow the naming convention.",
                Detail = $"The convention would put this bundle under '{expected}'. Fine if the folder was made on purpose; otherwise copy the bundle to the conventional folder (the current one is left as it is).",
                Fix = BundleNoticeFix.CopyToConventionalFolder, FixLabel = "Copy to the conventional folder",
            });
        }

        // 3. Legacy layout: saving migrates it.
        var legacy = new List<string>();
        if (!string.IsNullOrEmpty(configPath)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty), root, StringComparison.OrdinalIgnoreCase))
            legacy.Add("Config.json and the scripts at the root instead of Script\\");
        if (Directory.Exists(Path.Combine(root, ".git")))
            legacy.Add(".git at the root instead of Script\\.git");
        var shortcuts = BundlePaths.ShortcutDir(root);
        if (Directory.Exists(shortcuts) && Directory.EnumerateFiles(shortcuts, "*.cmd").Any())
            legacy.Add(".cmd shortcuts instead of .lnk");
        if (legacy.Count > 0)
        {
            list.Add(new BundleNotice
            {
                Id = "legacy-layout", Level = BundleNoticeLevel.Info,
                Title = "This bundle uses an older layout.",
                Detail = string.Join("; ", legacy) + ". The next save migrates it; nothing to do by hand.",
            });
        }

        // 4 and 5. Where the .intunewin goes.
        var output = config.Script.IntunePackager.Output;
        if (!string.IsNullOrWhiteSpace(output.Path))
        {
            var computed = IntuneWinOutput.Compute(settings, app, output);
            if (!string.Equals(output.Path.Trim(), computed, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new BundleNotice
                {
                    Id = "output-changes", Level = BundleNoticeLevel.Info,
                    Title = "The .intunewin output folder will change on the next save.",
                    Detail = $"Config.json says '{output.Path}'; Settings and this bundle's choice now give '{computed}'. A run before the next save still uses the old one.",
                });
            }

            var resolved = IntuneWinOutput.Resolve(output.Path, root);
            if (Path.IsPathRooted(resolved) && !IntuneWinOutput.RootReachable(resolved))
            {
                list.Add(new BundleNotice
                {
                    Id = "output-unreachable", Level = BundleNoticeLevel.Warning,
                    Title = "The .intunewin output folder is not reachable from this computer.",
                    Detail = $"'{Path.GetPathRoot(resolved)}' does not answer. A run from here writes to '{IntuneWinOutput.BesideBundle(root)}' instead and the preflight says so.",
                    Fix = BundleNoticeFix.OpenIntuneOutput, FixLabel = "Open the Output card",
                });
            }
        }

        return list;
    }

    /// <summary>True when the real path ends with the convention's folders (case-insensitive, segment by segment).</summary>
    public static bool FollowsConvention(string bundleRoot, string expectedRelative)
    {
        var actual = Path.TrimEndingDirectorySeparator(bundleRoot).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var expected = expectedRelative.Split('\\', '/').Where(s => s.Length > 0).ToArray();
        if (expected.Length == 0 || expected.Length > actual.Length) return false;
        for (var i = 1; i <= expected.Length; i++)
            if (!string.Equals(actual[^i], expected[^i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
