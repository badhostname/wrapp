using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// The token-mapping preview: per-token occurrence tracking in Expand, count
/// merging across fields, and the redaction contract - a sensitive value must
/// never appear in the summary text shown to the operator.
/// </summary>
// Swaps the static PlaceholderService.CustomsSource hook - serialized with
// the other placeholder test classes.
[Collection("Placeholders")]
public class PlaceholderTokenPreviewTests : IDisposable
{
    private static readonly AppSection App = new() { Name = "TestApp", Company = "Contoso" };

    public PlaceholderTokenPreviewTests()
        => PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();

    public void Dispose()
        => PlaceholderService.CustomsSource = () => Array.Empty<(string, string?, bool)>();

    private static void SetCustoms(params (string Name, string? Value, bool Sensitive)[] customs)
        => PlaceholderService.CustomsSource = () => customs;

    [Fact]
    public void Expand_TracksPerTokenCountsValuesAndOutcomes()
    {
        SetCustoms(("grp", "gid-1", false), ("pilot", "", false), ("vault", "s3cret", true));

        PlaceholderService.Expand(
            "{{Name}} {{Name}} {{grp}} {{pilot}} {{mystery}} {{vault}}", App, out var report);

        var name = Assert.Single(report.Tokens, t => t.Name == "Name");
        Assert.Equal(2, name.Count);
        Assert.Equal("TestApp", name.Value);
        Assert.Equal(PlaceholderTokenOutcome.Replaced, name.Outcome);

        var grp = Assert.Single(report.Tokens, t => t.Name == "grp");
        Assert.Equal(1, grp.Count);
        Assert.Equal("gid-1", grp.Value);

        var pilot = Assert.Single(report.Tokens, t => t.Name == "pilot");
        Assert.Equal(PlaceholderTokenOutcome.LeftEmpty, pilot.Outcome);

        var mystery = Assert.Single(report.Tokens, t => t.Name == "mystery");
        Assert.Equal(PlaceholderTokenOutcome.LeftUnknown, mystery.Outcome);

        var vault = Assert.Single(report.Tokens, t => t.Name == "vault");
        Assert.True(vault.IsSensitive);
    }

    [Fact]
    public void Merge_SumsTokenCountsAcrossFields()
    {
        SetCustoms(("grp", "gid-1", false));
        var f1 = new PlaceholderFieldAccessor("f1", () => "{{grp}} {{grp}}", _ => { });
        var f2 = new PlaceholderFieldAccessor("f2", () => "{{grp}} {{Name}}", _ => { });

        PlaceholderApplyService.ComputeChanges(new[] { f1, f2 }, App, out var aggregate);

        Assert.Equal(3, Assert.Single(aggregate.Tokens, t => t.Name == "grp").Count);
        Assert.Equal(1, Assert.Single(aggregate.Tokens, t => t.Name == "Name").Count);
    }

    [Fact]
    public void SummaryMarkdown_MapsTokens_AndRedactsSensitiveValues()
    {
        SetCustoms(("grp", "gid-1", false), ("vault", "s3cret-plaintext", true));
        var f = new PlaceholderFieldAccessor("f", () => "{{grp}} {{grp}} {{vault}}", _ => { });
        PlaceholderApplyService.ComputeChanges(new[] { f }, App, out var aggregate);

        var text = PlaceholderApplyService.BuildSummaryMarkdown(aggregate, 1);

        Assert.Contains("{{grp}}", text);
        Assert.Contains("×2", text);
        Assert.Contains("gid-1", text);
        Assert.Contains("••••••••", text);
        // The redaction contract: sensitive plaintext never enters the dialog.
        Assert.DoesNotContain("s3cret-plaintext", text);
    }

    [Fact]
    public void TruncateValue_CapsLongValues()
    {
        var longValue = new string('x', 200);
        var shown = PlaceholderApplyService.TruncateValue(longValue);
        Assert.Equal(81, shown.Length);
        Assert.EndsWith("…", shown);
        Assert.Equal("short", PlaceholderApplyService.TruncateValue("short"));
    }
}
