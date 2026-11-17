using Wrapp.Models;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// Exercises the generic clipboard formatter introduced in 0.6.0.0174
/// (<see cref="InventoryViewModel.ClipboardSection"/>) plus the
/// per-section Build*Body shape methods that drive both the per-section
/// copy button and the bottom "Copy All" export. Everything here is
/// deterministic string transformation - no PS, no Graph, no WPF.
/// </summary>
public class ClipboardSectionTests
{
    // ── ClipboardSection.Flat ────────────────────────────────────────

    [Fact]
    public void Flat_EmitsLabelColonValue_OnePerNonEmptyField()
    {
        var result = InventoryViewModel.ClipboardSection.Flat(
            ("Name", "Alice"),
            ("Age",  30));

        Assert.Equal("Name: Alice\r\nAge: 30", result);
    }

    [Fact]
    public void Flat_SkipsNullAndEmptyStrings()
    {
        var result = InventoryViewModel.ClipboardSection.Flat(
            ("Kept",       "value"),
            ("NullDrop",   (object?)null),
            ("EmptyDrop",  ""),
            ("WhitespaceDrop", "   "));

        // Null + empty drop; whitespace-only passes through (same
        // behaviour as the production Format helper for strings).
        Assert.Contains("Kept: value", result);
        Assert.DoesNotContain("NullDrop", result);
        Assert.DoesNotContain("EmptyDrop", result);
    }

    [Theory]
    [InlineData(true,  "Yes")]
    [InlineData(false, "No")]
    public void Flat_BoolsNormaliseToYesNo(bool input, string expected)
    {
        var result = InventoryViewModel.ClipboardSection.Flat(("Flag", input));
        Assert.Equal($"Flag: {expected}", result);
    }

    [Fact]
    public void Flat_ZeroValuedIntIsDropped()
    {
        // Convention from the ClipboardSection.Format helper: 0 means
        // "unset" for the numeric fields the builders emit (min OS,
        // processors, disk MB, etc.) so we omit them entirely.
        var result = InventoryViewModel.ClipboardSection.Flat(
            ("Visible",  5),
            ("Hidden",   0));

        Assert.Contains("Visible: 5", result);
        Assert.DoesNotContain("Hidden", result);
    }

    [Fact]
    public void Flat_NoFields_ReturnsEmptyString()
    {
        Assert.Equal("", InventoryViewModel.ClipboardSection.Flat());
    }

    [Fact]
    public void Flat_TrailingNewlinesTrimmed()
    {
        var result = InventoryViewModel.ClipboardSection.Flat(("A", "1"));
        Assert.Equal("A: 1", result);
        Assert.False(result.EndsWith("\n") || result.EndsWith("\r"));
    }

    // ── ClipboardSection.ListOfObjects ──────────────────────────────

    [Fact]
    public void ListOfObjects_EmptyCollection_ReturnsDefaultNoneText()
    {
        var empty = Array.Empty<string>();

        var result = InventoryViewModel.ClipboardSection.ListOfObjects(
            empty, _ => new (string, object?)[] { ("Item", "x") });

        Assert.Equal("None", result);
    }

    [Fact]
    public void ListOfObjects_EmptyCollection_CustomEmptyText()
    {
        var empty = Array.Empty<string>();

        var result = InventoryViewModel.ClipboardSection.ListOfObjects(
            empty, _ => new (string, object?)[] { ("x", "y") },
            emptyText: "(no entries)");

        Assert.Equal("(no entries)", result);
    }

    [Fact]
    public void ListOfObjects_MultipleItems_SeparatedByBlankLine_CardsIndented()
    {
        var items = new[] { "A", "B" };

        var result = InventoryViewModel.ClipboardSection.ListOfObjects(
            items,
            s => new (string, object?)[]
            {
                ("Name",   s),
                ("Length", s.Length),
            });

        Assert.Equal(
            "  Name: A\r\n  Length: 1\r\n\r\n  Name: B\r\n  Length: 1",
            result);
    }

    [Fact]
    public void ListOfObjects_MissingPerItemFields_AreOmitted()
    {
        var items = new[] { ("A", "desc"), ("B", "") };

        var result = InventoryViewModel.ClipboardSection.ListOfObjects(
            items,
            x => new (string, object?)[]
            {
                ("Name", x.Item1),
                ("Desc", x.Item2),
            });

        // First card has both lines; second card drops the empty Desc.
        Assert.Contains("  Name: A\r\n  Desc: desc", result);
        Assert.Contains("  Name: B", result);
        Assert.DoesNotContain("B\r\n  Desc:", result);
    }

    // ── Build*Body - whole-pipeline regression tests ────────────────

    [Fact]
    public void BuildDependedOnByBody_ContainsAppIdAndAutoInstall()
    {
        // User-reported issue: DependedOnBy was copying just "Name (Type)";
        // 0.6.0.0174 fixed this so GUID and Auto Install both land in the
        // output. This test locks that fix in.
        var detail = new AppInventoryDetail();
        detail.DependedOnBy.Add(new InventoryRelationshipInfo
        {
            AppName     = "Dell Command Configure",
            AppId       = "aaaa-bbbb-cccc",
            Type        = "Dependency",
            AutoInstall = true,
        });

        var body = InventoryViewModel.BuildDependedOnByBody(detail);

        Assert.Contains("App Name: Dell Command Configure", body);
        Assert.Contains("App ID: aaaa-bbbb-cccc",          body);
        Assert.Contains("Type: Dependency",                body);
        Assert.Contains("Auto Install: Yes",               body);
    }

    [Fact]
    public void BuildDependenciesBody_EmptyList_ReturnsNone()
    {
        Assert.Equal("None",
            InventoryViewModel.BuildDependenciesBody(new AppInventoryDetail()));
    }

    [Fact]
    public void BuildSupersedenceBody_EmitsUninstallPrevious()
    {
        var d = new AppInventoryDetail();
        d.Supersedence.Add(new InventoryRelationshipInfo
        {
            AppName = "Old App",
            AppId   = "deadbeef",
            Type    = "replace",      // UninstallLabel => "Yes"
        });

        var body = InventoryViewModel.BuildSupersedenceBody(d);

        Assert.Contains("App Name: Old App",        body);
        Assert.Contains("App ID: deadbeef",         body);
        Assert.Contains("Type: replace",            body);
        Assert.Contains("Uninstall Previous: Yes",  body);
    }

    [Fact]
    public void BuildAppInfoBody_Intune_IncludesIntuneOnlyFields()
    {
        var d = new AppInventoryDetail
        {
            Platform             = AppPlatform.Intune,
            DisplayName          = "My App",
            Id                   = "guid-1",
            Publisher            = "Contoso",
            Version              = "1.2.3",
            IsFeatured           = true,
            Developer            = "DevTeam",
            InformationUrl       = "https://info",
        };

        var body = InventoryViewModel.BuildAppInfoBody(d);

        Assert.Contains("Name: My App",          body);
        Assert.Contains("ID: guid-1",            body);
        Assert.Contains("Publisher: Contoso",    body);
        Assert.Contains("Version: 1.2.3",        body);
        Assert.Contains("Featured: Yes",         body);
        Assert.Contains("Developer: DevTeam",    body);
        Assert.Contains("Info URL: https://info", body);
        // SCCM-only field must NOT appear on an Intune card.
        Assert.DoesNotContain("Technology:",     body);
    }

    [Fact]
    public void BuildAppInfoBody_Sccm_IncludesSccmOnlyFields()
    {
        var d = new AppInventoryDetail
        {
            Platform                = AppPlatform.SCCM,
            DisplayName             = "7-Zip",
            Id                      = "sccm-id",
            Publisher               = "Igor Pavlov",
            Technology              = "Script Installer",
            IsEnabled               = true,
            NumberOfDeploymentTypes = 2,
            ObjectPath              = "/Applications/Utilities",
        };

        var body = InventoryViewModel.BuildAppInfoBody(d);

        Assert.Contains("Technology: Script Installer", body);
        Assert.Contains("Status: Enabled",              body);
        Assert.Contains("Deployment Types: 2",          body);
        Assert.Contains("Console Folder: /Applications/Utilities", body);
        // Intune-only field must NOT appear on an SCCM card.
        Assert.DoesNotContain("Featured:",              body);
    }

    [Fact]
    public void BuildAssignmentsBody_IncludesSourceMarker()
    {
        // Fix shipped in 0.6.0.0174 -- Source was missing from the copy.
        var d = new AppInventoryDetail();
        d.Assignments.Add(new InventoryAssignmentInfo
        {
            Intent       = "required",
            GroupMode    = "Include",
            TargetLabel  = "All-Corp-Devices",
            GroupId      = "00000000-0000-0000-0000-000000000001",
            Source       = "direct",
        });

        var body = InventoryViewModel.BuildAssignmentsBody(d);

        Assert.Contains("Source: direct", body);
    }
}
