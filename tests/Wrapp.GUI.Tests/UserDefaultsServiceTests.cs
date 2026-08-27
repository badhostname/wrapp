using System.IO;
using System.Text.Json;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Tests for <see cref="UserDefaultsService"/> — the shared
/// user-defaults.json sidecar that gives the PowerShell module access to the
/// user's package/assignment defaults and endpoint paths (CLI/UI parity) —
/// and for the {{TagFolder}}/{{LocalAppFolder}} endpoint token expansion.
///
/// NOTE: UserDefaultsService holds static state (Init). Tests that depend on
/// it live in this single class so xUnit's per-class serialization keeps them
/// from racing a parallel class's Init.
/// </summary>
public class UserDefaultsServiceTests
{
    [Fact]
    public async Task Export_WritesContractShape()
    {
        var settings = new AppSettings
        {
            EndpointTagFolder      = @"C:\Contoso\Tag",
            EndpointLocalAppFolder = @"C:\Contoso",
        };
        settings.IntuneMetadataDefaults.OwnerTemplate = "{{Company}} IT";
        settings.IntuneAssignmentDefaults.Notification = "showAll";

        var path = Path.Combine(Path.GetTempPath(), $"wrapp-ud-{Guid.NewGuid():N}.json");
        try
        {
            await UserDefaultsService.ExportAsync(settings, path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // Property names are a CONTRACT with the module's
            // Get-WrappUserDefaults / Merge-WrappUserDefaults.
            Assert.Equal(@"C:\Contoso\Tag", root.GetProperty("Endpoint").GetProperty("TagFolder").GetString());
            Assert.Equal(@"C:\Contoso",     root.GetProperty("Endpoint").GetProperty("LocalAppFolder").GetString());
            Assert.Equal("{{Company}} IT",  root.GetProperty("IntuneMetadataDefaults").GetProperty("OwnerTemplate").GetString());
            Assert.Equal("showAll",         root.GetProperty("IntuneAssignmentDefaults").GetProperty("Notification").GetString());
            Assert.True(root.TryGetProperty("IntunePackageDefaults", out _));
            Assert.True(root.TryGetProperty("SccmPackageDefaults", out _));
            Assert.True(root.TryGetProperty("SccmMetadataDefaults", out _));
            Assert.True(root.TryGetProperty("SccmDeploymentDefaults", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_EmptyEndpointFallsBackToSeed()
    {
        var settings = new AppSettings { EndpointTagFolder = "", EndpointLocalAppFolder = " " };
        var path = Path.Combine(Path.GetTempPath(), $"wrapp-ud-{Guid.NewGuid():N}.json");
        try
        {
            await UserDefaultsService.ExportAsync(settings, path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(ModuleDefaultsSeed.EndpointTagFolder,
                doc.RootElement.GetProperty("Endpoint").GetProperty("TagFolder").GetString());
            Assert.Equal(ModuleDefaultsSeed.EndpointLocalAppFolder,
                doc.RootElement.GetProperty("Endpoint").GetProperty("LocalAppFolder").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyTokens_ExpandsEndpointPathsFromSettings()
    {
        UserDefaultsService.Init(new AppSettings
        {
            EndpointTagFolder      = @"C:\Custom\Tag",
            EndpointLocalAppFolder = @"D:\Apps",
        });
        try
        {
            var app = new AppSection { Company = "Acme", Name = "Widget" };
            var result = TemplateService.ApplyTokens(
                "$TagFolder = \"{{TagFolder}}\"; $Dir = \"{{LocalAppFolder}}\\{{Name}}\"", app);

            Assert.Equal("$TagFolder = \"C:\\Custom\\Tag\"; $Dir = \"D:\\Apps\\Widget\"", result);
        }
        finally
        {
            // Restore fallback state (AppSettings property defaults ARE the seeds).
            UserDefaultsService.Init(new AppSettings());
        }
    }

    [Fact]
    public void ApplyTokens_EndpointTokensFallBackToSeedsWhenUnset()
    {
        UserDefaultsService.Init(new AppSettings { EndpointTagFolder = "", EndpointLocalAppFolder = "" });
        try
        {
            var app = new AppSection();
            var result = TemplateService.ApplyTokens("{{TagFolder}}|{{LocalAppFolder}}", app);
            Assert.Equal($"{ModuleDefaultsSeed.EndpointTagFolder}|{ModuleDefaultsSeed.EndpointLocalAppFolder}", result);
        }
        finally
        {
            UserDefaultsService.Init(new AppSettings());
        }
    }
}
