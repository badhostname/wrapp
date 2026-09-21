using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// The What's-New popup. On the first launch after ANY version
/// change (Velopack update, MSI pushed by Intune/SCCM, manual copy - the
/// mechanism doesn't matter, only the version does), shows the CHANGELOG
/// sections newer than the last version the user has seen, rendered through
/// the same markdown renderer as the help popups.
/// <para>
/// The changelog travels as an embedded resource, so the notes are always
/// exactly the ones for the running build - no feed call, works offline.
/// Dismissal (not display) records <see cref="AppSettings.LastSeenChangelogVersion"/>;
/// deliberately separate from <see cref="AppSettings.LastRunVersion"/>, which
/// GateService overwrites every launch.
/// </para>
/// </summary>
public static class WhatsNewService
{
    private const string ResourceName = "Wrapp.CHANGELOG.md";

    /// <summary>
    /// Default target of the "View all releases" link (About dialog and the
    /// What's-New popup). The embedded changelog only shows recent sections;
    /// this points at the complete history. Org defaults can override it via
    /// Update.ReleasesUrl (see <see cref="OrgUpdateDefaults.ReleasesUrl"/>).
    /// </summary>
    internal const string DefaultReleasesUrl =
        "https://github.com/badhostname/wrapp/blob/main/CHANGELOG.md";

    /// <summary>
    /// Resolved releases link: org override when set, else the default.
    /// The override reaches Process.Start(UseShellExecute) via a
    /// clicked markdown link, and unlike the feed/vault URLs it has no trust
    /// gate - so only accept it when it is genuinely https.
    /// </summary>
    public static string ReleasesUrl =>
        DefaultsLoader.LoadCached().Update?.ReleasesUrl is { Length: > 0 } org
        && Uri.TryCreate(org, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? org : DefaultReleasesUrl;

    /// <summary>Heading shape: "## [0.6.296-beta] - 2026-07-29" (also matches the legacy 4-part versions).</summary>
    private static readonly Regex SectionHeading = new(
        @"^##\s*\[(?<ver>[^\]]+)\]", RegexOptions.Compiled);

    /// <summary>Cap for pathological cases (corrupt marker, huge gap) so the popup stays readable.</summary>
    internal const int MaxSections = 15;

    /// <summary>
    /// Decides whether to show, shows, and records dismissal. Call on the
    /// startup path after blocking gates so the waiver always comes first.
    /// <paramref name="previousRunVersion"/> is <see cref="AppSettings.LastRunVersion"/>
    /// captured BEFORE GateService.RecordRun overwrote it this launch - it
    /// distinguishes a brand-new profile (empty: nothing to announce, just
    /// record) from a profile that predates this feature (non-empty: show the
    /// current version's notes once).
    /// </summary>
    public static async Task MaybeShowAsync(AppSettings settings, string previousRunVersion)
    {
        var current = AppInfo.Version;
        if (string.Equals(settings.LastSeenChangelogVersion, current, StringComparison.Ordinal))
            return;

        if (string.IsNullOrEmpty(settings.LastSeenChangelogVersion) && string.IsNullOrEmpty(previousRunVersion))
        {
            // First-ever launch on this profile: nothing was "updated" - baseline silently.
            settings.LastSeenChangelogVersion = current;
            SettingsService.Save(settings);
            return;
        }

        string markdown;
        try
        {
            markdown = ExtractSectionsSince(LoadChangelog(), settings.LastSeenChangelogVersion);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"WhatsNew: could not build release notes: {ex.Message}");
            markdown = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(markdown))
        {
            var resourceSource = System.Windows.Application.Current?.MainWindow as System.Windows.FrameworkElement;
            if (resourceSource is null) return; // no UI yet - try again next launch
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(BuildVersionCards(markdown, resourceSource));
            panel.Children.Add(BuildReleasesLink(resourceSource));
            await FluentDialog.ShowScrollableContentAsync($"What's new in Wrapp {AppInfo.VersionDisplay}", panel);
            AppLogger.Info($"WhatsNew: showed notes up to v{current}");
        }

        settings.LastSeenChangelogVersion = current;
        SettingsService.Save(settings);
    }

    /// <summary>
    /// One themed card per version: each "## [version]" section renders through
    /// the shared help markdown renderer, wrapped in a Border using the same
    /// theme brushes the renderer's tables use - so consecutive versions read
    /// as clearly separated blocks instead of one continuous document.
    /// </summary>
    private static System.Windows.FrameworkElement BuildVersionCards(
        string markdown, System.Windows.FrameworkElement resourceSource)
    {
        var panel = new System.Windows.Controls.StackPanel();
        foreach (var section in SplitSections(markdown))
        {
            var card = new System.Windows.Controls.Border
            {
                CornerRadius    = new System.Windows.CornerRadius(8),
                BorderThickness = new System.Windows.Thickness(1),
                Padding         = new System.Windows.Thickness(14, 6, 14, 10),
                Margin          = new System.Windows.Thickness(0, 0, 0, 12),
                Child           = HelpMarkdownRenderer.Render(section, resourceSource),
            };
            // Dynamic references so the cards follow live theme switches, like
            // every other themed surface.
            card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "InputBgBrush");
            card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "AppBorderBrush");
            panel.Children.Add(card);
        }
        return panel;
    }

    /// <summary>
    /// Recent-changes panel for the About dialog: the newest
    /// <paramref name="maxVersions"/> changelog sections as themed version
    /// cards. Capped because About should stay snappy - the full history is
    /// one click away via <see cref="BuildReleasesLink"/>.
    /// </summary>
    public static System.Windows.FrameworkElement BuildRecentChangesPanel(
        System.Windows.FrameworkElement resourceSource, int maxVersions = 10)
    {
        string markdown;
        try { markdown = LoadChangelog(); }
        catch (Exception ex)
        {
            AppLogger.Warn($"WhatsNew: changelog unavailable for About: {ex.Message}");
            return new System.Windows.Controls.StackPanel();
        }

        // Drop the "---" separator rules the changelog uses between entries -
        // same cleanup ExtractSectionsSince applies for the update popup.
        var recent = SplitSections(markdown)
            .Take(maxVersions)
            .Select(s => string.Join("\n", s.Split('\n').Where(l => l.Trim() != "---")));
        return BuildVersionCards(string.Join("\n", recent), resourceSource);
    }

    /// <summary>
    /// "View the full release history" hyperlink, themed like the markdown
    /// renderer's links; opens <see cref="ReleasesUrl"/> in the browser.
    /// </summary>
    public static System.Windows.FrameworkElement BuildReleasesLink(
        System.Windows.FrameworkElement resourceSource)
        => HelpMarkdownRenderer.Render(
            $"[View the full release history]({ReleasesUrl})", resourceSource);

    /// <summary>Splits joined sections back apart on the "## [" version headings.</summary>
    internal static List<string> SplitSections(string markdown)
    {
        var sections = new List<List<string>>();
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (SectionHeading.IsMatch(line)) sections.Add(new List<string>());
            if (sections.Count > 0) sections[^1].Add(line);
        }
        return sections
            .Select(s => string.Join("\n", s).TrimEnd())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>Reads the embedded changelog. Internal seam for tests.</summary>
    internal static string LoadChangelog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{ResourceName}' missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Returns the "## [version]" sections from the top of the changelog down
    /// to (excluding) <paramref name="lastSeenVersion"/>'s section. An empty or
    /// unknown marker yields only the newest section - never the entire
    /// history. Capped at <see cref="MaxSections"/>.
    /// </summary>
    internal static string ExtractSectionsSince(string changelog, string lastSeenVersion)
    {
        var lines = changelog.Replace("\r\n", "\n").Split('\n');

        var sections = new List<List<string>>();
        List<string>? current = null;
        var foundMarker = false;
        foreach (var line in lines)
        {
            var m = SectionHeading.Match(line);
            if (m.Success)
            {
                var ver = m.Groups["ver"].Value.Trim();
                if (string.Equals(ver, lastSeenVersion, StringComparison.Ordinal))
                {
                    foundMarker = true;
                    break;
                }
                if (sections.Count == MaxSections) break;
                current = new List<string>();
                sections.Add(current);
            }
            if (current is not null) current.Add(line);
        }

        if (sections.Count == 0) return string.Empty;

        // Everything collected is strictly newer than the marker - but only
        // when the marker was actually reached. An empty or unknown marker
        // (fresh field, renumbered history, cap hit first) shows just the
        // newest section rather than dumping history.
        var take = foundMarker ? sections.Count : 1;

        var sb = new StringBuilder();
        foreach (var sectionLines in sections.Take(take))
        {
            foreach (var l in sectionLines)
            {
                // The changelog separates entries with "---"; the renderer
                // would draw stray rules mid-popup, so drop them.
                if (l.Trim() == "---") continue;
                sb.AppendLine(l);
            }
        }
        return sb.ToString().TrimEnd();
    }
}
