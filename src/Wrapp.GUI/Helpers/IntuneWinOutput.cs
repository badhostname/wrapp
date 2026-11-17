using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Helpers;

/// <summary>
/// Where a bundle's <c>.intunewin</c> is written. The module wraps once per
/// run and every package in the pass shares the file, so the output folder
/// is a property of the bundle: <c>Script.IntunePackager.Output</c> in
/// Config.json. Wrapp computes <c>Output.Path</c> here at every save, from
/// Settings or from the bundle's own choice; the module reads only that
/// field (relative paths are relative to the bundle root), so a CLI run
/// lands where the GUI said without any settings on that machine.
/// <para>Beside the bundle is stored as the relative <c>..\Output</c>, which
/// travels with the bundle; a central or custom-nested path is built from
/// Company, Name and Version plus the chosen root, none of which change when
/// a folder moves. The next save fixes a settings change or a version bump.</para>
/// </summary>
public static class IntuneWinOutput
{
    public const string ModeBesideBundle = "BesideBundle";
    public const string ModeCentral = "Central";
    public static readonly string[] Modes = { ModeBesideBundle, ModeCentral };

    public const string SourceSettings = "Settings";
    public const string SourceCustom = "Custom";

    public const string LayoutNested = "Nested";
    public const string LayoutDirect = "Direct";

    /// <summary>Beside the bundle: an Output folder next to the version folder, so the next run does not wrap the previous file.</summary>
    public const string BesideBundleRelativePath = @"..\Output";

    /// <summary>Settings say Central and name a root.</summary>
    public static bool IsCentral(AppSettings settings)
        => string.Equals(settings.IntuneWinOutputMode, ModeCentral, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(settings.IntuneWinOutputRoot);

    public static string ModeLabel(AppSettings settings) => IsCentral(settings) ? "Central folder" : "Beside the bundle";

    /// <summary>
    /// The sub-folder the Settings format produces for <paramref name="app"/>
    /// under the output spaces rule; empty when the format is blank, or when
    /// it would not be a relative folder path (an empty token, <c>..</c>).
    /// </summary>
    public static string SubFolder(AppSettings settings, AppSection app)
    {
        var fmt = (settings.IntuneWinSubFolderFormat ?? string.Empty).Trim();
        if (fmt.Length == 0) return string.Empty;
        var rule = settings.IntuneWinFolderNameSpaces;
        var filled = fmt
            .Replace("{Company}",    BundleService.FolderName(app.Company, rule))
            .Replace("{Name}",       BundleService.FolderName(app.Name, rule))
            .Replace("{Version}",    BundleService.FolderName(app.Version, rule))
            .Replace("{DotVersion}", BundleService.FolderName(app.DotVersion, rule))
            .Replace("{Language}",   BundleService.FolderName(app.Language, rule))
            .Replace('/', '\\');
        // An empty token drops its folder rather than leaving a hole: the file
        // still lands under the root, one level shallower.
        var sub = string.Join('\\', filled.Split('\\').Select(s => s.Trim()).Where(s => s.Length > 0));
        if (sub.Length == 0) return string.Empty;
        return BundleService.RelativeFolderPathError(sub, "The output sub-folder format") is null ? sub : string.Empty;
    }

    /// <summary>What Settings resolve to: <c>..\Output</c>, or the central root plus the sub-folder.</summary>
    public static string FromSettings(AppSettings settings, AppSection app)
        => IsCentral(settings)
            ? Combine(settings.IntuneWinOutputRoot.Trim(), SubFolder(settings, app))
            : BesideBundleRelativePath;

    /// <summary>A bundle's own folder: the browsed root, nested under it with the Settings sub-folder or directly in it.</summary>
    public static string FromCustom(AppSettings settings, AppSection app, string root, string? layout)
        => string.Equals(layout, LayoutDirect, StringComparison.OrdinalIgnoreCase)
            ? root.Trim()
            : Combine(root.Trim(), SubFolder(settings, app));

    /// <summary>The path for an Output block as it stands: its custom root and layout, or Settings.</summary>
    public static string Compute(AppSettings settings, AppSection app, IntuneOutputSection output)
        => string.Equals(output.Source, SourceCustom, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(output.Root)
            ? FromCustom(settings, app, output.Root, output.Layout)
            : FromSettings(settings, app);

    /// <summary>
    /// Writes <c>Output.Path</c> for a config about to be saved. A Custom
    /// source without a root means nothing was chosen and reverts to Settings.
    /// </summary>
    public static void Apply(AppSettings settings, AppConfigModel config)
    {
        var output = config.Script.IntunePackager.Output;
        if (string.Equals(output.Source, SourceCustom, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(output.Root))
            output.Source = SourceSettings;
        if (string.IsNullOrWhiteSpace(output.Layout))
            output.Layout = LayoutNested;
        output.Path = Compute(settings, config.App, output);
    }

    /// <summary>
    /// A stored path as the run would use it: an absolute one as is, a
    /// relative one joined to the bundle root and normalised. Without a
    /// bundle root (a draft) the relative path is returned unchanged.
    /// </summary>
    public static string Resolve(string? path, string? bundleRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Trim();
        if (Path.IsPathRooted(p)) return p;
        if (string.IsNullOrWhiteSpace(bundleRoot)) return p;
        try { return Path.GetFullPath(Path.Combine(bundleRoot, p)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    /// <summary>The folder "beside the bundle" names for a bundle root: its parent's Output folder.</summary>
    public static string BesideBundle(string? bundleRoot) => Resolve(BesideBundleRelativePath, bundleRoot);

    /// <summary>True when the drive or share root of an absolute path exists from this computer.</summary>
    public static bool RootReachable(string absolutePath)
    {
        try
        {
            var root = Path.GetPathRoot(absolutePath);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    private static string Combine(string root, string sub)
        => sub.Length == 0 ? root : Path.Combine(root, sub);
}
