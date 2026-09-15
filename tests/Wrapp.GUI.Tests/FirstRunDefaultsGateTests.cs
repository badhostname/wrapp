using System.IO;
using System.Text.Json;
using Wrapp.Models;
using Wrapp.Services;
using Wrapp.Services.Gates;

namespace Wrapp.Tests;

/// <summary>
/// First-run organization provisioning: the gate that offers "browse for your
/// company defaults file" vs "use the built-in examples", and the durable
/// storage location that makes an imported file survive app updates (the
/// Velopack <c>current\</c> folder is replaced wholesale on every update).
/// </summary>
public class FirstRunDefaultsGateTests
{
    [Fact]
    public void Gate_NotPending_OnceAnswered()
    {
        var gate = new FirstRunDefaultsGate();
        var settings = new AppSettings();
        settings.GateState["first-run-defaults"] = "examples";
        Assert.False(gate.IsPending(settings));
    }

    [Fact]
    public void CandidatePaths_IncludeInstallRootAndProgramData()
    {
        var paths = DefaultsLoader.CandidatePaths().ToList();
        Assert.Contains(paths, p => p.EndsWith("defaults.local.json", StringComparison.OrdinalIgnoreCase));

        // The install-root probe is what makes a defaults file survive updates:
        // there must be a candidate ABOVE the app directory.
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Assert.Contains(paths, p =>
        {
            var dir = Path.GetDirectoryName(p)?.TrimEnd(Path.DirectorySeparatorChar);
            return dir is not null && dir.Length < appDir.Length && appDir.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        });

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Assert.Contains(paths, p => p.StartsWith(programData, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryImport_RejectsInvalidJson()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wrapp-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, "{ this is not json ");
        try
        {
            Assert.False(FirstRunDefaultsGate.TryImport(tmp, out var error));
            Assert.Contains("not valid JSON", error);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void TryImport_RejectsMissingFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"wrapp-missing-{Guid.NewGuid():N}.json");
        Assert.False(FirstRunDefaultsGate.TryImport(missing, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryImport_AcceptsValidDefaults_AndCopiesToDurablePath()
    {
        var org = new OrgDefaults
        {
            Update = new OrgUpdateDefaults { FeedUrl = @"\\server\feed", Mode = "NotifyOnly" },
            SensitivePatterns = { "CONTOSO" },
        };
        var tmp = Path.Combine(Path.GetTempPath(), $"wrapp-good-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, JsonSerializer.Serialize(org));

        var target = DefaultsLoader.DurableDefaultsPath();
        var existed = File.Exists(target);
        var backup = existed ? File.ReadAllText(target) : null;
        try
        {
            Assert.True(FirstRunDefaultsGate.TryImport(tmp, out var error), error);
            Assert.True(File.Exists(target));

            var round = JsonSerializer.Deserialize<OrgDefaults>(File.ReadAllText(target), JsonDefaults.CaseInsensitive);
            Assert.NotNull(round);
            Assert.Equal(@"\\server\feed", round!.Update?.FeedUrl);
        }
        finally
        {
            File.Delete(tmp);
            if (backup is not null) File.WriteAllText(target, backup);
            else if (File.Exists(target)) File.Delete(target);
        }
    }
}
