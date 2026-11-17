using Wrapp.Models;
using Wrapp.Services;

namespace Wrapp.Tests;

public class ScriptFrameworkShortcutTests
{
    /// <summary>
    /// The arguments as Appease sees them: [Environment]::GetCommandLineArgs()
    /// splits on spaces outside quotes and drops the quotes.
    /// </summary>
    private static List<string> CommandLineArgs(string arguments)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in arguments)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted)
            {
                if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) args.Add(current.ToString());
        return args;
    }

    [Theory]
    [InlineData("Install.lnk", "InstallScript.ps1")]
    [InlineData("Uninstall.lnk", "UninstallScript.ps1")]
    public void AppeaseShortcut_GivesTheElevatedRelaunchAnArgumentToRewrite(string lnkName, string script)
    {
        var (_, target, arguments) = Assert.Single(
            ScriptFrameworkProvider.GetShortcuts(ScriptFramework.Appease), s => s.LnkName == lnkName);

        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", target);
        Assert.Equal($@"-noexit -ExecutionPolicy Bypass -command ""&..\Script\{script}""", arguments);

        // Appease.ps1 rewrites only the argument matching ^& to the script's
        // full path when it relaunches elevated (in System32). Exactly one
        // must match, and it must name the script.
        var rewritten = Assert.Single(CommandLineArgs(arguments), a => a.StartsWith('&'));
        Assert.EndsWith($@"\Script\{script}", rewritten);
        Assert.Contains("-noexit", CommandLineArgs(arguments));
    }

    [Theory]
    [InlineData("Install.lnk", @"-ExecutionPolicy Bypass -File ""..\Script\InstallScript.ps1""", true)]
    [InlineData("Uninstall.lnk", @"-ExecutionPolicy Bypass -File ""..\Script\UninstallScript.ps1""", true)]
    [InlineData("Install.lnk", @"-ExecutionPolicy Bypass -File ""..\Script\UninstallScript.ps1""", false)]
    [InlineData("Install.lnk", @"-noexit -ExecutionPolicy Bypass -command ""&..\Script\InstallScript.ps1""", false)]
    [InlineData("Install.lnk", @"-ExecutionPolicy Bypass -File ""..\Script\InstallScript.ps1"" -Verbose", false)]
    public void OnlyTheExactEarlierDefault_IsReplaced(string lnkName, string existing, bool superseded) =>
        Assert.Equal(superseded, ScriptFrameworkProvider.IsSupersededShortcut(ScriptFramework.Appease, lnkName, existing));

    [Fact]
    public void PsadtShortcuts_AreNeverTreatedAsSuperseded() =>
        Assert.False(ScriptFrameworkProvider.IsSupersededShortcut(ScriptFramework.PSADT, "Install.lnk",
            @"-ExecutionPolicy Bypass -File ""..\Script\InstallScript.ps1"""));
}
