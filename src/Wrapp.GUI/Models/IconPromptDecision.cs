namespace Wrapp.Models;

/// <summary>
/// When an installer brings a new icon, how aggressively to ask before
/// replacing the current one. Each entry point declares its policy instead of
/// re-implementing the check (feature/icon-selector).
/// </summary>
public enum IconPromptPolicy
{
    /// <summary>Always take the incoming icon silently (e.g. bundle imports, where the imported icon IS the truth).</summary>
    Never,

    /// <summary>
    /// Prompt only when the current icon was a deliberate choice
    /// (<see cref="AppSection.IconUserChosen"/>). Full installer applies use
    /// this: routine installer-over-installer replaces the auto icon silently,
    /// but a browsed/library icon is protected.
    /// </summary>
    WhenUserChosen,

    /// <summary>
    /// Prompt whenever ANY current icon exists. Upgrade and icon-only
    /// extraction use this (their historical behavior).
    /// </summary>
    WhenAnyCurrentIcon,
}

/// <summary>
/// The pure "should this prompt?" decision, kept UI-free so the policy matrix
/// is unit-testable. The caller has already established that an incoming icon
/// exists.
/// </summary>
public static class IconPromptDecision
{
    public static bool ShouldPrompt(bool hasCurrentIcon, bool iconUserChosen, IconPromptPolicy policy)
        => hasCurrentIcon && policy switch
        {
            IconPromptPolicy.Never              => false,
            IconPromptPolicy.WhenUserChosen     => iconUserChosen,
            IconPromptPolicy.WhenAnyCurrentIcon => true,
            _                                   => false,
        };
}
