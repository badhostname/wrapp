using System.IO;
using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Covers <see cref="BundleService.DetectInstallersInBinaryFolder"/> added
/// in 0.6.0.0180 as a fill-if-empty fallback for Import-to-Wrapp (and
/// future paths that populate a bundle without going through drag-drop
/// detection). Uses real temp directories - the helper is pure file I/O.
/// </summary>
public class BundleServiceTests : IDisposable
{
    private readonly string _root;

    public BundleServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WrappTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppConfigModel NewConfig(string framework)
    {
        var c = new AppConfigModel();
        c.App.ScriptFramework = framework;
        return c;
    }

    // ── Empty / missing ─────────────────────────────────────────────

    [Fact]
    public void EmptyBinFolder_ReturnsNoneNone()
    {
        Directory.CreateDirectory(Path.Combine(_root, "B"));
        var (exe, msi) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Null(exe);
        Assert.Null(msi);
    }

    [Fact]
    public void MissingBinFolder_ReturnsNoneNone_DoesNotThrow()
    {
        var (exe, msi) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Null(exe);
        Assert.Null(msi);
    }

    // ── Single-file detection ───────────────────────────────────────

    [Fact]
    public void SingleExe_IsDetected()
    {
        var bin = Path.Combine(_root, "B");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "installer.exe"), "bytes");

        var (exe, msi) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Equal("installer.exe", exe);
        Assert.Null(msi);
    }

    [Fact]
    public void SingleMsi_IsDetected()
    {
        var bin = Path.Combine(_root, "B");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "package.msi"), "bytes");

        var (exe, msi) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Null(exe);
        Assert.Equal("package.msi", msi);
    }

    [Fact]
    public void MspIsTreatedAsMsi()
    {
        // .msp is a patch file -- treated interchangeably with .msi here.
        var bin = Path.Combine(_root, "B");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "patch.msp"), "bytes");

        var (exe, msi) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Null(exe);
        Assert.Equal("patch.msp", msi);
    }

    [Fact]
    public void BothExeAndMsi_AreDetected()
    {
        var bin = Path.Combine(_root, "B");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "installer.exe"), "bytes");
        File.WriteAllText(Path.Combine(bin, "package.msi"),   "bytes");

        var (exe, msi) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Equal("installer.exe", exe);
        Assert.Equal("package.msi",   msi);
    }

    // ── Multiple candidates → deterministic pick ───────────────────

    [Fact]
    public void MultipleExe_ReturnsAlphabeticallyFirst()
    {
        // Determinism matters for tests and for user expectations -- the
        // result must be the same every time the helper runs on the same
        // folder.
        var bin = Path.Combine(_root, "B");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "zulu.exe"),  "bytes");
        File.WriteAllText(Path.Combine(bin, "alpha.exe"), "bytes");
        File.WriteAllText(Path.Combine(bin, "mid.exe"),   "bytes");

        var (exe, _) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Equal("alpha.exe", exe);
    }

    // ── Framework-awareness ─────────────────────────────────────────

    [Fact]
    public void Psadt_ScansFilesFolderNotB()
    {
        // Appease uses B/, PSADT uses Files/. A PSADT bundle with an EXE
        // in Files/ must be detected; an EXE in B/ (unexpected layout)
        // should NOT be picked up because PSADT's bin folder is Files/.
        var filesDir = Path.Combine(_root, "Files");
        Directory.CreateDirectory(filesDir);
        File.WriteAllText(Path.Combine(filesDir, "app.exe"), "bytes");

        var (exe, _) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("PSADT"));
        Assert.Equal("app.exe", exe);
    }

    [Fact]
    public void NonInstallerFiles_AreIgnored()
    {
        // PDFs, ZIPs, text files etc. shouldn't match either slot.
        var bin = Path.Combine(_root, "B");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "readme.txt"),  "text");
        File.WriteAllText(Path.Combine(bin, "archive.zip"), "bytes");
        File.WriteAllText(Path.Combine(bin, "docs.pdf"),    "bytes");

        var (exe, msi) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Null(exe);
        Assert.Null(msi);
    }

    // ── GetBundleRoot: path-shape defense ──────────────────────────

    [Theory]
    [InlineData(@"C:\Bundles\App\1.0.0\Script\Config.json", @"C:\Bundles\App\1.0.0")]
    [InlineData(@"C:\Bundles\App\1.0.0\Config.json",        @"C:\Bundles\App\1.0.0")]  // legacy root layout
    [InlineData(@"C:\Apps\script\Config.json",              @"C:\Apps")]               // lowercase OK
    public void GetBundleRoot_ResolvesCommonLayouts(string configPath, string expected)
    {
        Assert.Equal(expected, BundleService.GetBundleRoot(configPath));
    }

    [Fact]
    public void FirstExeBeatsLaterMsp_ForExeSlot()
    {
        // Ensures the scan doesn't conflate slots: alphabetically first
        // .exe goes to the exe slot, alphabetically first .msi/.msp goes
        // to the msi slot -- independently.
        var bin = Path.Combine(_root, "B");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "b-installer.exe"), "bytes");
        File.WriteAllText(Path.Combine(bin, "a-patch.msp"),     "bytes");
        File.WriteAllText(Path.Combine(bin, "c-package.msi"),   "bytes");

        var (exe, msi) = BundleService.DetectInstallersInBinaryFolder(_root, NewConfig("Appease"));
        Assert.Equal("b-installer.exe", exe);
        Assert.Equal("a-patch.msp",     msi);   // .msp alphabetically before .msi
    }

    // ── Sanitize: path-injection defense ───────────────────────────
    // Sanitize runs on individual token VALUES (Company/Name/Version/...),
    // never on the directory-format template itself. So a value must never
    // be able to inject a path separator or a parent-traversal segment --
    // the only separators in the final path should be the literal ones the
    // operator put in DirectoryFormat. This mirrors the contract that the
    // sibling sanitiser VaultPathTemplate.Sanitize already enforces (and
    // whose doc comment cites BundleService as the "path-style precedent").

    [Fact]
    public void Sanitize_StripsParentTraversalSegment()
    {
        // A Version auto-extracted from an installer's FileVersionInfo, or a
        // fat-fingered Company, could contain "..". It must not survive as a
        // usable traversal segment.
        var result = BundleService.Sanitize(@"..\..\Windows");
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void Sanitize_StripsEmbeddedBackslashFromValue()
    {
        // A single token's value must collapse to a single path segment --
        // it cannot introduce its own folder boundary.
        var result = BundleService.Sanitize(@"Contoso\Evil");
        Assert.DoesNotContain('\\', result);
    }

    [Fact]
    public void Sanitize_StripsEmbeddedForwardSlashFromValue()
    {
        var result = BundleService.Sanitize("Contoso/Evil");
        Assert.DoesNotContain('/', result);
    }

    [Fact]
    public void Sanitize_KeepsOrdinaryName()
    {
        // Regression guard: legitimate values pass through untouched.
        Assert.Equal("Contoso Ltd", BundleService.Sanitize("Contoso Ltd"));
        Assert.Equal("1.2.3.4",     BundleService.Sanitize("1.2.3.4"));
    }

    [Fact]
    public void ResolveSubDirectory_TraversalInCompany_CannotEscapeBaseDirectory()
    {
        // The real flow: a malicious/garbage Company value must not let the
        // resolved bundle directory climb above the chosen save root.
        var settings = new AppSettings();               // default DirectoryFormat = {Company}\{Name}\{Version}
        var app = new AppConfigModel().App;
        app.Company = @"..\..\..\..\Windows\System32";
        app.Name    = "Calc";
        app.Version = "1.0.0";

        var rel = BundleService.ResolveSubDirectory(settings, app);

        var baseDir = Path.Combine(_root, "Saves");
        var full    = Path.GetFullPath(Path.Combine(baseDir, rel));
        var baseFull = Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar;

        Assert.StartsWith(baseFull, full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
