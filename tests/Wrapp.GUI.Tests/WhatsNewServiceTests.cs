using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Workstream D5: changelog slicing for the What's-New popup. Invariants: the
/// user sees exactly the sections newer than the last version they dismissed;
/// an empty or unrecognisable marker shows only the newest section (never the
/// whole history); the embedded changelog resource actually exists and parses.
/// </summary>
public class WhatsNewServiceTests
{
    private const string Changelog = """
        # Wrapp Changelog

        Header prose.

        ---

        ## [0.6.298-beta] - 2026-07-30

        ### Newest thing

        - newest bullet

        ---

        ## [0.6.297-beta] - 2026-07-29

        ### Middle thing

        - middle bullet

        ---

        ## [0.6.296-beta] - 2026-07-29

        ### Oldest thing

        - oldest bullet
        """;

    [Fact]
    public void MarkerInMiddle_ReturnsOnlyNewerSections()
    {
        var md = WhatsNewService.ExtractSectionsSince(Changelog, "0.6.296-beta");
        Assert.Contains("Newest thing", md);
        Assert.Contains("Middle thing", md);
        Assert.DoesNotContain("Oldest thing", md);
        Assert.DoesNotContain("Header prose", md);
    }

    [Fact]
    public void MarkerIsNewest_ReturnsEmpty()
    {
        Assert.Equal(string.Empty,
            WhatsNewService.ExtractSectionsSince(Changelog, "0.6.298-beta"));
    }

    [Fact]
    public void EmptyMarker_ReturnsNewestSectionOnly()
    {
        var md = WhatsNewService.ExtractSectionsSince(Changelog, "");
        Assert.Contains("Newest thing", md);
        Assert.DoesNotContain("Middle thing", md);
    }

    [Fact]
    public void UnknownMarker_ReturnsNewestSectionOnly()
    {
        var md = WhatsNewService.ExtractSectionsSince(Changelog, "9.9.9");
        Assert.Contains("Newest thing", md);
        Assert.DoesNotContain("Middle thing", md);
    }

    [Fact]
    public void LegacyFourPartMarker_IsMatched()
    {
        var log = """
            ## [0.6.296-beta] - 2026-07-29

            New scheme entry.

            ## [0.6.0.0295-beta] - 2026-07-29

            Old scheme entry.
            """;
        var md = WhatsNewService.ExtractSectionsSince(log, "0.6.0.0295-beta");
        Assert.Contains("New scheme entry", md);
        Assert.DoesNotContain("Old scheme entry", md);
    }

    [Fact]
    public void SeparatorRules_AreStripped()
    {
        var md = WhatsNewService.ExtractSectionsSince(Changelog, "0.6.296-beta");
        Assert.DoesNotContain("---", md);
    }

    [Fact]
    public void SplitSections_OneEntryPerVersionHeading()
    {
        var md = WhatsNewService.ExtractSectionsSince(Changelog, "0.6.296-beta");
        var sections = WhatsNewService.SplitSections(md);

        // Each card gets exactly one version; content stays with its heading.
        Assert.Equal(2, sections.Count);
        Assert.StartsWith("## [0.6.298-beta]", sections[0]);
        Assert.Contains("newest bullet", sections[0]);
        Assert.StartsWith("## [0.6.297-beta]", sections[1]);
        Assert.Contains("middle bullet", sections[1]);
    }

    [Fact]
    public void SplitSections_IgnoresPreambleBeforeFirstHeading()
    {
        var sections = WhatsNewService.SplitSections("prose before any heading\n## [1.0.0] - x\nbody");
        Assert.Single(sections);
        Assert.StartsWith("## [1.0.0]", sections[0]);
    }

    [Fact]
    public void EmbeddedChangelog_LoadsAndContainsCurrentVersion()
    {
        var log = WhatsNewService.LoadChangelog();
        Assert.Contains($"## [{AppInfo.Version}]", log);
    }
}
