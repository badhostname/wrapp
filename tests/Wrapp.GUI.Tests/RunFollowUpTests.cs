using System.Management.Automation;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Gates;

namespace Wrapp.Tests;

/// <summary>
/// Clean-up notices from runs that failed part-way: read from the module's
/// tagged object, shown by the action-needed gate, kept per machine.
/// </summary>
public class RunFollowUpTests
{
    private static PSObject ModuleNotice()
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("_Type", "WrappFollowUp"));
        o.Properties.Add(new PSNoteProperty("Target", "Intune"));
        o.Properties.Add(new PSNoteProperty("TargetId", "d35fe7ad-abdf-4422-8ef9-8234b4c7a904"));
        o.Properties.Add(new PSNoteProperty("Package", "Snagit"));
        o.Properties.Add(new PSNoteProperty("Step", "AppCreation"));
        o.Properties.Add(new PSNoteProperty("Summary", "The upload to Intune failed after 3 attempt(s); the app exists in Intune without content."));
        o.Properties.Add(new PSNoteProperty("Reason", "Intune service request for operation 'AzureStorageUriRequest' failed"));
        // PowerShell hands arrays over as object[] (possibly wrapped).
        o.Properties.Add(new PSNoteProperty("Actions", new PSObject(new object[]
        {
            "Delete the incomplete app 'Snagit' (ID 9c3c48c0-a32b-4cb2-8636-46d6f38e5fc6) in the Intune portal.",
            @"The package file is still at Z:\Applications\TechSmith\Snagit\Output\Snagit_26_4_0.intunewin.",
        })));
        return o;
    }

    [Fact]
    public void A_module_notice_is_read_with_its_actions_and_anything_else_is_ignored()
    {
        var at = new DateTimeOffset(2026, 11, 10, 19, 32, 50, TimeSpan.Zero);
        var f = RunFollowUp.FromPSObject(ModuleNotice(), at, @"Z:\Applications\TechSmith\Snagit\26_4_0");

        Assert.NotNull(f);
        Assert.Equal("Snagit", f!.Package);
        Assert.Equal("App creation", f.StepLabel);
        Assert.Equal(2, f.Actions.Count);
        Assert.StartsWith("Delete the incomplete app", f.Actions[0]);
        Assert.Equal(at, f.RaisedAt);
        Assert.Contains(f.LogLines(), l => l.StartsWith("[ACTION NEEDED] Snagit (Intune): the run failed at step 'App creation'."));
        Assert.Contains(f.LogLines(), l => l.Contains("Reason: Intune service request"));

        var step = new PSObject();
        step.Properties.Add(new PSNoteProperty("_Type", "WrappStep"));
        Assert.Null(RunFollowUp.FromPSObject(step, at, ""));
    }

    [Fact]
    public void The_dialog_names_the_step_the_reason_and_the_likely_actions()
    {
        var f = RunFollowUp.FromPSObject(ModuleNotice(), DateTimeOffset.UtcNow, @"Z:\Bundles\Snagit")!;
        var md = RunFollowUp.ToMarkdown(new[] { f });
        Assert.Contains("## Snagit - Intune d35fe7ad", md);
        Assert.Contains("Failed at step **App creation**", md);
        Assert.Contains("Reason: Intune service request", md);
        Assert.Contains("- Delete the incomplete app 'Snagit'", md);
        Assert.Contains("Mark these done once you have cleaned up", md);
    }

    [Fact]
    public void The_gate_is_lit_while_notices_are_open_and_counts_them_in_its_title()
    {
        var settings = new AppSettings();
        var gate = new RunFollowUpGate();
        Assert.False(gate.IsPending(settings));

        settings.RunFollowUps.Add(new RunFollowUp { Package = "Snagit" });
        Assert.True(gate.IsPending(settings));
        Assert.Equal("Clean up after a failed run", gate.Title);

        settings.RunFollowUps.Add(new RunFollowUp { Package = "7-Zip" });
        Assert.True(gate.IsPending(settings));
        Assert.Equal("Clean up after 2 failed runs", gate.Title);

        RunFollowUpStore.Clear(settings);
        Assert.False(gate.IsPending(settings));
    }

    [Fact]
    public void Notices_stay_on_this_machine_and_are_not_exported()
    {
        var settings = new AppSettings();
        settings.RunFollowUps.Add(new RunFollowUp { Package = "Snagit", Summary = "half-created app" });
        var json = SettingsPortability.BuildExportJson(settings);
        Assert.DoesNotContain("RunFollowUps", json);
        Assert.DoesNotContain("half-created app", json);
    }
}
