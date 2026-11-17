using System.IO;
using Wrapp.Helpers;
using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// The file side of the one save pipeline: what the bundle copy carries and
/// leaves behind, and how a file that something else still holds is retried,
/// then reported rather than thrown.
/// </summary>
public class SavePipelineTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _dest;

    public SavePipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WrappTests_" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "src");
        _dest = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_source);
    }

    public void Dispose()
    {
        FileRetry.WaitAsync = ms => Task.Delay(ms);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Seed(params string[] relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var path = Path.Combine(_source, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rel);
        }
    }

    private static readonly BundleCopyOptions Appease = new("B");

    [Fact]
    public async Task The_copy_keeps_binary_sub_folders_and_leaves_config_git_and_lock_behind()
    {
        Seed(@"Script\Config.json", @"Script\Config.json.bak", @"Script\Config.json.tmp", @"Script\InstallScript.ps1",
             @"Script\.git\HEAD", @"B\setup.exe", @"B\drivers\x64\oemsetup.inf", @"Shortcuts\Install.lnk",
             @"Icon\app.png", ".lock", @".git.backup-123\config", "Config.json");

        var result = await BundleCopyService.CopyAsync(_source, _dest, Appease);

        Assert.Empty(result.Failed);
        Assert.Equal(5, result.Copied.Count);
        Assert.True(File.Exists(Path.Combine(_dest, @"B\drivers\x64\oemsetup.inf")), "a sub-folder of B\\ must arrive");
        Assert.True(File.Exists(Path.Combine(_dest, @"B\setup.exe")));
        Assert.True(File.Exists(Path.Combine(_dest, @"Shortcuts\Install.lnk")));
        Assert.True(File.Exists(Path.Combine(_dest, @"Icon\app.png")));
        Assert.True(File.Exists(Path.Combine(_dest, @"Script\InstallScript.ps1")));
        Assert.False(File.Exists(Path.Combine(_dest, @"Script\Config.json")), "the pipeline writes Config.json from the model; the copy must not write it first");
        Assert.False(File.Exists(Path.Combine(_dest, @"Script\Config.json.bak")));
        Assert.False(File.Exists(Path.Combine(_dest, @"Script\Config.json.tmp")));
        Assert.False(File.Exists(Path.Combine(_dest, "Config.json")), "a legacy root Config.json is not carried over");
        Assert.False(Directory.Exists(Path.Combine(_dest, @"Script\.git")));
        Assert.False(Directory.Exists(Path.Combine(_dest, ".git.backup-123")));
        Assert.False(File.Exists(Path.Combine(_dest, ".lock")));
    }

    [Fact]
    public async Task The_installer_at_the_binary_root_is_skipped_only_when_asked_and_sub_folders_still_come()
    {
        Seed(@"B\setup.exe", @"B\patch.msp", @"B\drivers\driver.msi", @"B\readme.txt");

        var result = await BundleCopyService.CopyAsync(_source, _dest, Appease with { SkipInstallersInBinaryRoot = true });

        Assert.Equal(new[] { @"B\drivers\driver.msi", @"B\readme.txt" }, result.Copied.OrderBy(x => x));
        Assert.False(File.Exists(Path.Combine(_dest, @"B\setup.exe")));
    }

    [Fact]
    public void IsSharingViolation_recognises_the_two_win32_codes_and_nothing_else()
    {
        Assert.True(FileRetry.IsSharingViolation(new IOException("in use", unchecked((int)0x80070020))));
        Assert.True(FileRetry.IsSharingViolation(new IOException("locked", unchecked((int)0x80070021))));
        Assert.False(FileRetry.IsSharingViolation(new IOException("gone", unchecked((int)0x80070002))));
        Assert.False(FileRetry.IsSharingViolation(new FileNotFoundException()));
        Assert.False(FileRetry.IsSharingViolation(new UnauthorizedAccessException()));
    }

    [Fact]
    public async Task A_file_held_open_for_a_moment_is_copied_once_the_holder_lets_go()
    {
        Seed(@"Shortcuts\Install.lnk");
        Directory.CreateDirectory(Path.Combine(_dest, "Shortcuts"));
        var held = Path.Combine(_dest, @"Shortcuts\Install.lnk");
        var waits = new List<int>();

        // Like a scanner: holds the just-written file exclusively and lets go
        // between two attempts. Released inside the first retry wait rather
        // than on a timer, so the test does not depend on thread-pool timing.
        FileStream? holder = new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.None);
        FileRetry.WaitAsync = ms => { waits.Add(ms); holder?.Dispose(); holder = null; return Task.CompletedTask; };
        try
        {
            var result = await BundleCopyService.CopyAsync(_source, _dest, Appease);
            Assert.Empty(result.Failed);
            Assert.Equal(new[] { @"Shortcuts\Install.lnk" }, result.Copied);
        }
        finally
        {
            holder?.Dispose();
        }
        Assert.Single(waits); // one refusal, one retry that succeeded
        Assert.Equal(@"Shortcuts\Install.lnk", File.ReadAllText(held));
    }

    [Fact]
    public async Task A_file_still_in_use_after_every_attempt_is_listed_and_the_rest_is_copied()
    {
        Seed(@"B\a.txt", @"B\b.txt", @"Shortcuts\Install.lnk");
        Directory.CreateDirectory(Path.Combine(_dest, "B"));
        var held = Path.Combine(_dest, @"B\b.txt");
        FileRetry.WaitAsync = _ => Task.CompletedTask;

        BundleCopyResult first;
        using (new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.None))
            first = await BundleCopyService.CopyAsync(_source, _dest, Appease);

        Assert.Equal(new[] { @"B\a.txt", @"Shortcuts\Install.lnk" }, first.Copied.OrderBy(x => x));
        var failure = Assert.Single(first.Failed);
        Assert.Equal(@"B\b.txt", failure.RelativePath);
        Assert.False(string.IsNullOrWhiteSpace(failure.Error));

        // Retry copies exactly the files that failed, now that the holder is gone.
        var again = await BundleCopyService.CopyFilesAsync(_source, _dest, first.Failed.Select(f => f.RelativePath).ToList());
        Assert.Empty(again.Failed);
        Assert.Equal(new[] { @"B\b.txt" }, again.Copied);
        Assert.Equal(@"B\b.txt", File.ReadAllText(held));
    }

    [Fact]
    public async Task A_config_write_outlasts_a_scanner_holding_the_previous_file()
    {
        var path = Path.Combine(_dest, @"Script\Config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"old\": true }");
        // The scanner lets go during the first retry wait (no timer, no race).
        FileStream? holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var waits = 0;
        FileRetry.WaitAsync = _ => { waits++; holder?.Dispose(); holder = null; return Task.CompletedTask; };
        try
        {
            await AtomicFile.WriteAllTextAsync(path, "{ \"new\": true }");
        }
        finally
        {
            holder?.Dispose();
        }
        Assert.Equal(1, waits);

        Assert.Equal("{ \"new\": true }", File.ReadAllText(path));
        Assert.Equal("{ \"old\": true }", File.ReadAllText(path + ".bak"));
    }
}
