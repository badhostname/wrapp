using System.Text.Json;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Validates the Phase-3 enum promotion contract: every promoted enum
/// serialises as its canonical Pascal-case member name and deserialises
/// case-insensitively. PowerShell-side comparisons against literal
/// strings ("Create", "Update", ...) must keep working after the C#
/// type change.
/// </summary>
public class EnumRoundTripTests
{
    [Theory]
    [InlineData(UpdateMode.Create,        "Create")]
    [InlineData(UpdateMode.Update,        "Update")]
    [InlineData(UpdateMode.UpdateContent, "UpdateContent")]
    public void UpdateMode_WritesCanonicalString(UpdateMode value, string expected)
    {
        var json = JsonSerializer.Serialize(value, JsonDefaults.PrettyUnsafe);
        Assert.Equal($"\"{expected}\"", json);
    }

    [Theory]
    [InlineData("\"Create\"",        UpdateMode.Create)]
    [InlineData("\"create\"",        UpdateMode.Create)]        // lowercase tolerated
    [InlineData("\"CREATE\"",        UpdateMode.Create)]        // screaming tolerated
    [InlineData("\"Update\"",        UpdateMode.Update)]
    [InlineData("\"UpdateContent\"", UpdateMode.UpdateContent)]
    [InlineData("\"updatecontent\"", UpdateMode.UpdateContent)]
    public void UpdateMode_ReadsCaseInsensitively(string json, UpdateMode expected)
    {
        var result = JsonSerializer.Deserialize<UpdateMode>(json, JsonDefaults.PrettyUnsafe);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void UpdateMode_UnknownString_ThrowsCleanJsonException()
    {
        var ex = Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<UpdateMode>("\"NotAMode\"", JsonDefaults.PrettyUnsafe));
        // Message names the enum type and lists the valid values so a user
        // reading an exception log can correct their input.
        Assert.Contains("UpdateMode", ex.Message);
        Assert.Contains("Create",     ex.Message);
    }

    [Theory]
    [InlineData(AuthFlow.Interactive,  "Interactive")]
    [InlineData(AuthFlow.DeviceCode,   "DeviceCode")]
    [InlineData(AuthFlow.ClientSecret, "ClientSecret")]
    [InlineData(AuthFlow.ClientCert,   "ClientCert")]
    public void AuthFlow_WritesCanonicalString(AuthFlow value, string expected)
    {
        var json = JsonSerializer.Serialize(value, JsonDefaults.PrettyUnsafe);
        Assert.Equal($"\"{expected}\"", json);
    }

    [Theory]
    [InlineData(AppPlatform.Intune, "Intune")]
    [InlineData(AppPlatform.SCCM,   "SCCM")]
    public void AppPlatform_WritesCanonicalString(AppPlatform value, string expected)
    {
        var json = JsonSerializer.Serialize(value, JsonDefaults.PrettyUnsafe);
        Assert.Equal($"\"{expected}\"", json);
    }

    [Fact]
    public void UpdateMode_IntegerValue_Rejected()
    {
        // Integer ordinals must NOT deserialise: it prevents accidental
        // data-corruption bugs when PowerShell serialises an enum as its
        // numeric value by mistake, and it rejects malicious inputs that
        // try to reach unnamed ordinals.
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<UpdateMode>("0", JsonDefaults.PrettyUnsafe));
    }

    [Fact]
    public void ConfigRoundTrip_IntunePackage_UpdateMode_Preserved()
    {
        // Exercises the full ConfigFileService parse/serialize pipeline
        // since that uses JsonValue.Create(...) rather than JsonSerializer
        // directly. The v2 envelope must still produce a string "Update"
        // that a future build (or the PowerShell packager) recognises.
        var original = new AppConfigModel();
        original.Script.IntunePackager.Packages.Add(new IntunePackageEntry
        {
            AppName    = "TestApp",
            UpdateMode = UpdateMode.UpdateContent,
            ExistingAppID = "fdb5eaa3-7a92-4769-ac43-40f853f70350",
        });

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Contains("\"UpdateMode\": \"UpdateContent\"", json);
        var pkg = Assert.Single(restored.Script.IntunePackager.Packages);
        Assert.Equal(UpdateMode.UpdateContent, pkg.UpdateMode);
    }

    [Fact]
    public void ConfigRoundTrip_IntuneTenant_AuthFlow_Preserved()
    {
        var original = new AppConfigModel();
        original.IntuneTenants.Add(new IntuneTenantEntry
        {
            Key      = "PROD",
            AuthFlow = AuthFlow.ClientCert,
        });

        var json     = ConfigFileService.SerializeToJson(original);
        var restored = ConfigFileService.DeserializeFromJson(json);

        Assert.Contains("\"AuthFlow\": \"ClientCert\"", json);
        Assert.Equal(AuthFlow.ClientCert, restored.IntuneTenants[0].AuthFlow);
    }
}
