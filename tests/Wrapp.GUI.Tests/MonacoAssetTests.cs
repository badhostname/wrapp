using System.IO;

namespace Wrapp.Tests;

/// <summary>
/// Guards the trimmed Monaco tree (scripts\Trim-Monaco.ps1). Re-vendoring
/// Monaco restores the full ~100-language build; the trim must be re-applied,
/// and it must never remove what Wrapp actually edits: PowerShell scripts,
/// Config.json, XML (detection rules / MSI tables), plaintext (diff + history
/// views), and the editor core itself.
/// </summary>
public class MonacoAssetTests
{
    private static string MonacoVsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Wrapp.GUI", "Assets", "monaco", "vs");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new DirectoryNotFoundException("Monaco vs directory not found walking up from the test base directory");
    }

    [Theory]
    [InlineData("powershell")]   // Install/Uninstall/Detect scripts, PSADT
    [InlineData("xml")]          // detection rule / manifest editing
    public void TrimmedTree_KeepsTheLanguagesWrappEdits(string language)
    {
        var vs = MonacoVsDir();
        var chunks = Directory.GetFiles(vs, $"{language}-*.js", SearchOption.TopDirectoryOnly);
        Assert.True(chunks.Length > 0, $"Monaco language chunk for '{language}' is missing -- re-run scripts\\Trim-Monaco.ps1 after re-vendoring");
    }

    [Fact]
    public void TrimmedTree_KeepsTheEditorCoreAndJsonService()
    {
        var vs = MonacoVsDir();
        Assert.True(File.Exists(Path.Combine(vs, "editor", "editor.main.js")), "editor.main.js missing");
        Assert.True(File.Exists(Path.Combine(vs, "loader.js")), "AMD-compat loader missing");
        Assert.True(Directory.Exists(Path.Combine(vs, "language", "json")), "JSON language service missing (Config.json editing)");
        // The editor's own worker must survive -- it backs every model.
        Assert.True(Directory.GetFiles(Path.Combine(vs, "assets"), "editor.worker-*.js").Length > 0,
            "editor worker missing");
    }

    [Fact]
    public void TrimmedTree_DropsTheHeavyUnusedServices()
    {
        var vs = MonacoVsDir();
        // Regression guard for payload size: TypeScript alone is ~13 MB across
        // its service folder and worker, and Wrapp never opens a TS model.
        Assert.False(Directory.Exists(Path.Combine(vs, "language", "typescript")), "TypeScript service should be trimmed");
        Assert.Empty(Directory.GetFiles(Path.Combine(vs, "assets"), "ts.worker-*.js"));
    }
}
