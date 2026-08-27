using System.IO;
using System.Text.RegularExpressions;

namespace Wrapp.Tests.Lint;

/// <summary>
/// Phase-9 capstone: source-level architectural invariants enforced as
/// xUnit facts. Every rule the team has converged on across the
/// optimisation phases is now mechanically checked, so future PRs that
/// violate them fail CI rather than slip in unnoticed.
///
/// Each rule is a single test that scans <c>src/Wrapp.GUI/</c> at runtime,
/// applies a rule-specific regex + allowlist, and asserts zero remaining
/// violations. On failure the message lists every <c>file:line</c> so
/// authors fix offenders directly.
///
/// New allowlist entries should always land with a code-comment
/// justification next to the entry below — not in the violator's source.
/// </summary>
public class SourceLintTests
{
    // --------------------------------------------------------------------
    // Source root + file enumeration
    // --------------------------------------------------------------------

    private static readonly string GuiSrcRoot = FindGuiSrcRoot();
    private static readonly string RepoRoot   = Directory.GetParent(GuiSrcRoot)!.Parent!.FullName;

    private static string FindGuiSrcRoot()
    {
        // Walk upward from the test binary until we find src/Wrapp.GUI/.
        // Robust to CI vs IDE working-directory differences.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Wrapp.GUI");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "SourceLintTests: could not locate src/Wrapp.GUI/ above the test binary "
            + $"(started at {AppContext.BaseDirectory}).");
    }

    private static readonly Lazy<List<(string Path, string[] Lines)>> _sourceCache =
        new(() => LoadGuiSources().ToList());

    private static IEnumerable<(string Path, string[] Lines)> Sources => _sourceCache.Value;

    private static IEnumerable<(string Path, string[] Lines)> LoadGuiSources()
    {
        var sep = Path.DirectorySeparatorChar;
        foreach (var f in Directory.EnumerateFiles(GuiSrcRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip MSBuild-generated trees; only authored code is in scope.
            if (f.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (f, File.ReadAllLines(f));
        }
    }

    private static string Rel(string fullPath)
        => Path.GetRelativePath(RepoRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static string ContainsView(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}Views{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}Controls{sep}", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
            ? "view-layer" : "";
    }

    // --------------------------------------------------------------------
    // Rule 1: empty `catch { }` must have a rationale comment above
    //
    // Truly-empty `catch { }` (or `catch (Exception) { }`,
    // `catch (Exception ex) { }`) is allowed only when the immediately
    // preceding non-blank line is a comment that explains the swallow.
    // The codebase convention is `// must-stay-silent: <why>` but we don't
    // pin the exact lead-in -- any `//` or block-comment line counts.
    // Inline-comment forms (`catch { /* explanation */ }`) and catches
    // with bodies are not flagged at all (different shape -- different rule).
    // --------------------------------------------------------------------

    private static readonly Regex EmptyCatchRegex = new(
        @"^\s*catch(?:\s*\(\s*Exception(?:\s+\w+)?\s*\))?\s*\{\s*\}\s*$",
        RegexOptions.Compiled);

    [Fact]
    public void NoUndocumentedEmptyCatch()
    {
        var violations = new List<string>();

        foreach (var (path, lines) in Sources)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (!EmptyCatchRegex.IsMatch(lines[i])) continue;
                if (HasCommentImmediatelyAbove(lines, i)) continue;
                violations.Add($"{Rel(path)}:{i + 1}");
            }
        }

        Assert.True(violations.Count == 0,
            "Empty catch blocks must have an explanatory comment immediately above. "
            + "Either add a `// must-stay-silent: <why>` comment line, or replace with a "
            + "typed catch that logs via AppLogger. Offenders:\n  "
            + string.Join("\n  ", violations));
    }

    private static bool HasCommentImmediatelyAbove(string[] lines, int catchLine)
    {
        for (int j = catchLine - 1; j >= 0; j--)
        {
            var prev = lines[j].TrimStart();
            if (prev.Length == 0) continue;
            // Any comment line above (single-line `//`, block-comment body, or
            // a closing `*/`) counts as documenting the swallow.
            return prev.StartsWith("//", StringComparison.Ordinal)
                || prev.StartsWith("*",  StringComparison.Ordinal)
                || prev.StartsWith("/*", StringComparison.Ordinal)
                || prev.EndsWith("*/",   StringComparison.Ordinal);
        }
        return false;
    }

    // --------------------------------------------------------------------
    // Rule 2: async void only on view-layer or event-handler signatures
    //
    // Forbidden shape: `async void DoStuffAsync()` on a service or VM.
    // The exception bubbles to AppDomain.UnhandledException with no
    // observable Task -- silent crashes. Allowed:
    //   - WPF code-behinds in Views/ Controls/ or *.xaml.cs files
    //   - Method signatures whose params include `EventArgs` (any flavour)
    //   - The deliberate fire-and-forget wrapper `SafeFireAndForget.cs`
    // --------------------------------------------------------------------

    private static readonly Regex AsyncVoidRegex = new(
        @"^\s*(?:(?:public|private|protected|internal|static|partial|override|virtual|sealed)\s+)*async\s+void\s+(?<name>\w+)\s*\((?<args>[^)]*)\)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> AsyncVoidAllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // The deliberate fire-and-forget wrapper. Its whole purpose is to
        // adapt a Task to async-void shape so unhandled exceptions surface
        // through the documented logging callback rather than the AppDomain.
        "SafeFireAndForget.cs",
    };

    [Fact]
    public void NoAsyncVoidOutsideEventHandlers()
    {
        var violations = new List<string>();

        foreach (var (path, lines) in Sources)
        {
            if (!string.IsNullOrEmpty(ContainsView(path))) continue;
            if (AsyncVoidAllowedFiles.Contains(Path.GetFileName(path))) continue;

            for (int i = 0; i < lines.Length; i++)
            {
                var m = AsyncVoidRegex.Match(lines[i]);
                if (!m.Success) continue;

                // Real WPF/UI event handlers always include an `EventArgs`
                // (RoutedEventArgs, MouseEventArgs, DpiChangedEventArgs, …)
                // in their parameter list. Anything else is an unobservable
                // foot-gun that should be `async Task` and awaited.
                if (m.Groups["args"].Value.Contains("EventArgs", StringComparison.Ordinal))
                    continue;

                // Cross-VM event subscribers in this codebase use the
                // convention `private async void OnXxx(...)` to subscribe to
                // events broadcast by sibling viewmodels. The OnXxx prefix
                // is the marker; the payload is sometimes a tuple rather
                // than EventArgs (e.g., `(AppConfigModel, string)`), so we
                // can't rely on EventArgs alone.
                if (m.Groups["name"].Value.StartsWith("On", StringComparison.Ordinal))
                    continue;

                violations.Add($"{Rel(path)}:{i + 1}");
            }
        }

        Assert.True(violations.Count == 0,
            "async void is forbidden outside View/Control code-behinds and event handlers "
            + "(parameter list must include EventArgs). Replace with `async Task` and await it, "
            + "or wrap deliberate fire-and-forget through SafeFireAndForget. Offenders:\n  "
            + string.Join("\n  ", violations));
    }

    // --------------------------------------------------------------------
    // Rule 3: file writes must not feed raw JsonSerializer.Serialize
    //
    // The dangerous pattern is `File.WriteAllText(path, JsonSerializer.Serialize(x))`
    // (and friends) without options -- defaults produce inconsistent
    // shape, no indentation, locale-sensitive number formatting. File
    // writes go through `ConfigFileService.SaveAsync` or pass an explicit
    // `JsonDefaults.Pretty` / `JsonDefaults.PrettyUnsafe` / `JsonDefaults.Compact`.
    //
    // This rule deliberately does NOT flag bare `JsonSerializer.Serialize`
    // outside file-write contexts: the Monaco services (JS-string escape),
    // EncryptionKeyStoreService HTTP bodies, and the equality-snapshot
    // helpers in SettingsViewModel/PreferencesViewModel are legitimate
    // single-arg callers where defaults are correct.
    // --------------------------------------------------------------------

    private static readonly Regex FileWriteWithRawSerializeRegex = new(
        @"\bFile\.Write\w+\s*\([^)]*JsonSerializer\s*\.\s*Serialize\b",
        RegexOptions.Compiled);

    [Fact]
    public void FileWriteMustNotConsumeRawSerialize()
    {
        var violations = new List<string>();

        foreach (var (path, lines) in Sources)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (FileWriteWithRawSerializeRegex.IsMatch(lines[i]))
                    violations.Add($"{Rel(path)}:{i + 1}");
            }
        }

        Assert.True(violations.Count == 0,
            "File.Write* must not feed JsonSerializer.Serialize directly -- default "
            + "options produce inconsistent file shape. Route through ConfigFileService.SaveAsync, "
            + "or call JsonSerializer.Serialize(x, JsonDefaults.Pretty) (or JsonDefaults.PrettyUnsafe / "
            + "JsonDefaults.Compact) explicitly. Offenders:\n  " + string.Join("\n  ", violations));
    }

    // --------------------------------------------------------------------
    // Rule 4: WindowInteropHelper allocations are centralised.
    //
    // Only `Helpers/WindowHelper.cs` is allowed to construct
    // `WindowInteropHelper`. Other code goes through
    // `WindowHelper.GetMainWindowHwnd()` and friends so the HWND-acquisition
    // contract is one-place-only.
    // --------------------------------------------------------------------

    private static readonly Regex WindowInteropHelperRegex = new(
        @"\bnew\s+WindowInteropHelper\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void WindowInteropHelperOnlyInWindowHelper()
    {
        var violations = new List<string>();

        foreach (var (path, lines) in Sources)
        {
            if (string.Equals(Path.GetFileName(path), "WindowHelper.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            for (int i = 0; i < lines.Length; i++)
            {
                if (WindowInteropHelperRegex.IsMatch(lines[i]))
                    violations.Add($"{Rel(path)}:{i + 1}");
            }
        }

        Assert.True(violations.Count == 0,
            "WindowInteropHelper allocations must live in Helpers/WindowHelper.cs. "
            + "Use WindowHelper.GetMainWindowHwnd() / WindowHelper.GetWindowHandle(window) instead. "
            + "Offenders:\n  " + string.Join("\n  ", violations));
    }

    // --------------------------------------------------------------------
    // Rule 5: Progress<T> allocations are centralised.
    //
    // Only `Services/Infrastructure/UiProgress.cs` is allowed to construct
    // `new Progress<T>(...)`. Other code uses `UiProgress.ForStatus(...)` /
    // `UiProgress.ForProgress(...)` so SyncContext capture happens in one
    // audited location.
    // --------------------------------------------------------------------

    private static readonly Regex ProgressRegex = new(
        @"\bnew\s+Progress\s*<",
        RegexOptions.Compiled);

    [Fact]
    public void NewProgressOnlyInUiProgress()
    {
        var violations = new List<string>();

        foreach (var (path, lines) in Sources)
        {
            if (string.Equals(Path.GetFileName(path), "UiProgress.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            for (int i = 0; i < lines.Length; i++)
            {
                if (ProgressRegex.IsMatch(lines[i]))
                    violations.Add($"{Rel(path)}:{i + 1}");
            }
        }

        Assert.True(violations.Count == 0,
            "new Progress<T>(...) must be allocated through "
            + "UiProgress.ForStatus / UiProgress.ForProgress (Services/Infrastructure/UiProgress.cs). "
            + "Direct allocations bypass the centralised SyncContext-capture contract. "
            + "Offenders:\n  " + string.Join("\n  ", violations));
    }

    // --------------------------------------------------------------------
    // Rule 6 (Phase 13 / D-1): bundle path layout is owned by BundlePaths.
    //
    // Inline `Path.Combine(..., "Script", "Config.json")` is a future
    // "scripts moved" rewrite trap -- collecting them in BundlePaths means
    // the layout is one edit away if it ever changes. This rule sweeps the
    // GUI source tree for the literal pair and rejects new occurrences
    // outside the helper itself. ConfigFileService legitimately uses the
    // string "Script" as a JSON property key (the section name inside
    // Config.json, not the folder), so the regex requires Path.Combine
    // context -- string-literal usage is fine.
    // --------------------------------------------------------------------

    private static readonly Regex BundlePathLiteralRegex = new(
        @"\bPath\.Combine\s*\([^)]*""Script""[^)]*""Config\.json""",
        RegexOptions.Compiled);

    [Fact]
    public void BundlePathCompositionMustGoThroughBundlePaths()
    {
        var violations = new List<string>();

        foreach (var (path, lines) in Sources)
        {
            if (string.Equals(Path.GetFileName(path), "BundlePaths.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            for (int i = 0; i < lines.Length; i++)
            {
                if (BundlePathLiteralRegex.IsMatch(lines[i]))
                    violations.Add($"{Rel(path)}:{i + 1}");
            }
        }

        Assert.True(violations.Count == 0,
            "Path.Combine(..., \"Script\", \"Config.json\") must go through BundlePaths.ConfigJson(...). "
            + "If you need a sibling helper (e.g. legacy-layout fallback), add it to BundlePaths.cs. "
            + "Offenders:\n  " + string.Join("\n  ", violations));
    }

    // --------------------------------------------------------------------
    // Rule 7 (Phase 14 / D-3): inline enum validation must go through
    // Test-EnumField in Test-WrappConfig.ps1.
    //
    // Phase 14 collapsed 26 copy-paste blocks of the shape:
    //
    //     if ($obj.Field -and $obj.Field -notin $D.ValidXxx) { ... Add-Error @p }
    //
    // into single Test-EnumField calls. Future contributors might
    // re-introduce the inline form -- the lint sweeps Test-WrappConfig.ps1
    // for the literal `-notin $D.Valid...` pattern and rejects new ones. The
    // few legitimate sites that need different shape (the
    // "if (-not $dr.Type -or $dr.Type -notin ...)" missing-OR-invalid path,
    // and the rare DetectionType / non-Existence DetectionMethod branches)
    // are exempt because they appear in `(-not ... -or ...)` form, which the
    // regex does not match.
    // --------------------------------------------------------------------

    private static readonly Regex InlineEnumValidationRegex = new(
        @"^\s*if\s*\(\s*\$\w+\.\w+\s+-and\s+\$\w+\.\w+\s+-notin\s+\$D\.",
        RegexOptions.Compiled);

    [Fact]
    public void PackagerConfigEnumValidationMustGoThroughTestEnumField()
    {
        var violations = new List<string>();
        var target = Path.Combine(
            RepoRoot, "modules", "Wrapp.Packager", "Public", "Test-WrappConfig.ps1");
        if (!File.Exists(target)) return; // skip when module not present

        var lines = File.ReadAllLines(target);
        for (int i = 0; i < lines.Length; i++)
        {
            if (InlineEnumValidationRegex.IsMatch(lines[i]))
                violations.Add($"modules/Wrapp.Packager/Public/Test-WrappConfig.ps1:{i + 1}");
        }

        Assert.True(violations.Count == 0,
            "Inline `if ($obj.Field -and $obj.Field -notin $D.Valid...)` enum validation must go "
            + "through Test-EnumField. Build a $p = @{ Value; ValidValues; FieldPath; Label; "
            + "Guidance; AddError } hashtable and call `Test-EnumField @p`. Offenders:\n  "
            + string.Join("\n  ", violations));
    }

    // --------------------------------------------------------------------
    // Rule 8 (Phase 11 / S-5): Wrapp.Packager Write-Log calls must not
    // interpolate variables that look like tokens.
    //
    // Even though Write-Log now runs through Redact-LogLine, the redaction
    // is a defence-in-depth net -- the primary discipline is "do not log
    // raw token-bearing variables in the first place". This rule fires on
    // direct interpolation patterns like `Write-Log "... $AccessToken ..."`
    // or `Write-Log "$AuthHeader"` so the offender shows up in CI before
    // the regex even has to run.
    //
    // Match shape: `Write-Log` followed by a $<word>Token or $<word>Secret
    // or $...AccessToken / $AuthenticationHeader / $...Bearer reference on
    // the same line. Conservative: only flags substring patterns we know
    // are real secrets. Non-token-named variables (e.g. $TokenCount) won't
    // match because the word "Token" must come right before whitespace,
    // quote, comma, paren, semicolon, or end-of-line.
    // --------------------------------------------------------------------

    private static readonly Regex PsTokenLogRegex = new(
        @"Write-Log\b[^\n]*\$[A-Za-z_][A-Za-z0-9_]*(?:Token|Secret|Bearer|AccessToken|RefreshToken|IdToken|AuthenticationHeader|ClientSecret|PAT)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void PackagerWriteLogMustNotInterpolateTokens()
    {
        var violations = new List<string>();
        var packagerRoot = Path.Combine(RepoRoot, "modules", "Wrapp.Packager");
        if (!Directory.Exists(packagerRoot))
        {
            // If the module isn't present (e.g. partial checkouts) skip the
            // rule rather than fail noisily. The build's CopyWrappPackager
            // target would have failed first if this were a problem.
            return;
        }

        foreach (var file in Directory.EnumerateFiles(packagerRoot, "*.ps1", SearchOption.AllDirectories))
        {
            // Exempt the redaction helper itself -- its purpose is to handle
            // these strings safely. Same for Write-Log (the call site of
            // Redact-LogLine, which legitimately formats the message).
            var name = Path.GetFileName(file);
            if (string.Equals(name, "Redact-LogLine.ps1", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, "Write-Log.ps1",       StringComparison.OrdinalIgnoreCase)) continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                if (PsTokenLogRegex.IsMatch(line))
                    violations.Add($"{Path.GetRelativePath(RepoRoot, file).Replace(Path.DirectorySeparatorChar, '/')}:{i + 1}");
            }
        }

        Assert.True(violations.Count == 0,
            "Write-Log must not interpolate token-bearing variables ($AccessToken, "
            + "$AuthenticationHeader, $ClientSecret, $RefreshToken, etc.) in Wrapp.Packager. "
            + "Redact-LogLine catches these at runtime but the discipline is to never log "
            + "them in the first place. If you genuinely need to log a length / shape, "
            + "log the property indirectly (e.g. ($Token.Length) not $Token). Offenders:\n  "
            + string.Join("\n  ", violations));
    }

    // --------------------------------------------------------------------
    // Rule: the %LOCALAPPDATA%\Wrapp root is resolved ONLY by PlatformConfig
    // --------------------------------------------------------------------
    // The production-readiness audit found 11 files re-deriving the app data
    // root by hand; two of them (MSAL cache path, settings path) silently
    // bypassed the env-var overrides PlatformConfig exists to honor — reading
    // files nobody writes when an override is set. Every path must come from
    // PlatformConfig.WrappRoot / its named subpaths.

    private static readonly Regex LocalAppDataRegex = new(
        @"SpecialFolder\.LocalApplicationData", RegexOptions.Compiled);

    [Fact]
    public void LocalApplicationDataOnlyInPlatformConfig()
    {
        var violations = new List<string>();

        foreach (var (path, lines) in Sources)
        {
            if (string.Equals(Path.GetFileName(path), "PlatformConfig.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            for (int i = 0; i < lines.Length; i++)
            {
                if (LocalAppDataRegex.IsMatch(lines[i]))
                    violations.Add($"{Rel(path)}:{i + 1}");
            }
        }

        Assert.True(violations.Count == 0,
            "Environment.SpecialFolder.LocalApplicationData may only be resolved in "
            + "Services/PlatformConfig.cs. Build paths from PlatformConfig.WrappRoot / "
            + "PlatformConfig.LockDir (etc.) so env-var and appsettings overrides keep "
            + "working. Offenders:\n  " + string.Join("\n  ", violations));
    }
}
