using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// feature/icon-selector: icon provenance (App.IconUserChosen) - the flag that
/// makes a Full installer apply ask before replacing a deliberately chosen
/// icon while leaving auto-extracted icons silently replaceable. Covers the
/// pure prompt-decision matrix and the Config.json round-trip (the protection
/// must survive save/reopen).
/// </summary>
public class IconProvenanceTests
{
    // ------------------------------------------------------------------
    // Prompt decision matrix
    // ------------------------------------------------------------------

    [Theory]
    // No current icon: never prompt, whatever the policy.
    [InlineData(false, false, IconPromptPolicy.Never,              false)]
    [InlineData(false, true,  IconPromptPolicy.WhenUserChosen,     false)]
    [InlineData(false, true,  IconPromptPolicy.WhenAnyCurrentIcon, false)]
    // Never: silent even over a chosen icon (bundle-import semantics).
    [InlineData(true,  true,  IconPromptPolicy.Never,              false)]
    // WhenUserChosen (Full apply): auto icon replaced silently, chosen icon prompts.
    [InlineData(true,  false, IconPromptPolicy.WhenUserChosen,     false)]
    [InlineData(true,  true,  IconPromptPolicy.WhenUserChosen,     true)]
    // WhenAnyCurrentIcon (Upgrade / icon-only): any current icon prompts.
    [InlineData(true,  false, IconPromptPolicy.WhenAnyCurrentIcon, true)]
    [InlineData(true,  true,  IconPromptPolicy.WhenAnyCurrentIcon, true)]
    public void ShouldPrompt_Matrix(bool hasCurrent, bool userChosen, IconPromptPolicy policy, bool expected)
    {
        Assert.Equal(expected, IconPromptDecision.ShouldPrompt(hasCurrent, userChosen, policy));
    }

    // ------------------------------------------------------------------
    // Config.json persistence
    // ------------------------------------------------------------------

    [Fact]
    public void IconUserChosen_RoundTrips_ThroughConfigJson()
    {
        var model = new AppConfigModel();
        model.App.Name = "TestApp";
        model.App.IconFile = @"Icon\TestApp.png";
        model.App.IconUserChosen = true;

        var reloaded = ConfigFileService.DeserializeFromJson(
            ConfigFileService.SerializeToJson(model));

        Assert.True(reloaded.App.IconUserChosen);
    }

    [Fact]
    public void IconUserChosen_DefaultsFalse_ForPreExistingBundles()
    {
        // Bundles authored before the field must keep replace-silently behavior.
        var model = ConfigFileService.DeserializeFromJson("""
            {
              "App": { "Name": "OldApp", "IconFile": "Icon\\OldApp.png" },
              "Script": {},
              "SCCMSite": {},
              "IntuneTenant": {},
              "Domain": {}
            }
            """);
        Assert.False(model.App.IconUserChosen);
    }

    [Fact]
    public void IconUserChosen_False_RoundTripsFalse()
    {
        var model = new AppConfigModel();
        model.App.Name = "TestApp";

        var reloaded = ConfigFileService.DeserializeFromJson(
            ConfigFileService.SerializeToJson(model));

        Assert.False(reloaded.App.IconUserChosen);
    }
}
