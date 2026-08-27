using System.IO;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Centralises the bundle directory layout. Every site that hard-codes the
/// <c>Script</c> + <c>Config.json</c> + binary-folder composition is a future
/// &#x201C;scripts moved&#x201D; search-and-replace target &#x2014; collecting them here means
/// the layout is one edit away if it ever changes (e.g., per-framework
/// subfolder rename, new bundle layout version).
///
/// Phase 13 (D-1). Migration target for ~14 prior call sites across
/// <see cref="BundleService"/>, <see cref="ScriptFrameworkProvider"/>,
/// <see cref="ConfigFileService"/>, and the General view-model partials.
/// </summary>
public static class BundlePaths
{
    /// <summary>Name of the subfolder that holds <c>Config.json</c> and the runtime scripts.</summary>
    public const string ScriptFolder = "Script";

    /// <summary>Canonical file name for the bundle config.</summary>
    public const string ConfigFileName = "Config.json";

    /// <summary>Subfolder for shortcuts (`Install.lnk`, `Uninstall.lnk`).</summary>
    public const string ShortcutFolder = "Shortcuts";

    /// <summary>
    /// Returns the full path to <c>Config.json</c> in the new (Script/) layout
    /// under <paramref name="bundleRoot"/>. Use <see cref="BundleService.FindConfigJson"/>
    /// when the caller needs to discover whether a bundle uses the new or old
    /// layout; use this helper when the new layout is known.
    /// </summary>
    public static string ConfigJson(string bundleRoot)
        => Path.Combine(bundleRoot, ScriptFolder, ConfigFileName);

    /// <summary>Returns the full path to the <c>Script/</c> directory under <paramref name="bundleRoot"/>.</summary>
    public static string ScriptDir(string bundleRoot)
        => Path.Combine(bundleRoot, ScriptFolder);

    /// <summary>Returns the full path to the <c>Shortcuts/</c> directory under <paramref name="bundleRoot"/>.</summary>
    public static string ShortcutDir(string bundleRoot)
        => Path.Combine(bundleRoot, ShortcutFolder);

    /// <summary>
    /// Returns the full path to the framework-specific binary folder
    /// (<c>B/</c> for Appease, <c>Files/</c> for PSADT) under
    /// <paramref name="bundleRoot"/>.
    /// </summary>
    public static string BinaryFolder(string bundleRoot, ScriptFramework framework)
        => Path.Combine(bundleRoot, ScriptFrameworkProvider.GetBinaryFolderName(framework));
}
