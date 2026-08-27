namespace Wrapp.Models;

/// <summary>
/// The three legal values of <see cref="AppSettings.UpdateMode"/> (the app's
/// own update policy - unrelated to the Intune package <c>UpdateMode</c>
/// enum). Previously restated as string literals in five files with nothing
/// keeping them aligned. Kept as string constants rather than an enum because
/// the value round-trips through settings.json and org defaults files that
/// older builds must keep reading.
/// </summary>
public static class AppUpdateModes
{
    /// <summary>Check at launch; enforce only on a sibling-free splash.</summary>
    public const string Auto = "Auto";
    /// <summary>Indicator-only, for fleets updated via Intune/SCCM supersedence.</summary>
    public const string NotifyOnly = "NotifyOnly";
    /// <summary>No automatic checks.</summary>
    public const string Disabled = "Disabled";

    /// <summary>ComboBox source (Settings → Updates).</summary>
    public static readonly string[] All = { Auto, NotifyOnly, Disabled };
}
