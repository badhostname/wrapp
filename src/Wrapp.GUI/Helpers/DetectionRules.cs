using Wrapp.Models;

namespace Wrapp.Helpers;

/// <summary>
/// The Detection view's consistency rules, kept pure so they can be tested
/// without WPF. Errors stop the bundle from detecting correctly (two tests
/// share a symbol, so substitution is ambiguous); warnings are inconsistent
/// but runnable:
/// <list type="bullet">
/// <item>tests exist but an expression is blank - nothing references them
/// there (a blank named expression silently falls back to Default);</item>
/// <item>an expression has content but there are no tests - every symbol
/// in it is undefined, so the script fails to evaluate;</item>
/// <item>a test has no symbol - no expression can reference it;</item>
/// <item>a test's symbol appears in no expression - the script never runs
/// it (often a typo, or a reference that was removed).</item>
/// </list>
/// The unused-symbol rule applies only once some expression has content;
/// with every expression blank, the blank-expression warning already says it.
/// </summary>
public static class DetectionRules
{
    /// <summary>
    /// True when <paramref name="expression"/> refers to <paramref name="symbol"/>:
    /// the symbol appears case-sensitively (the detect script substitutes with
    /// a case-sensitive <c>Replace</c>, so <c>a</c> never stands for <c>A</c>)
    /// and as a whole name - not inside a longer symbol (<c>A</c> in <c>AB</c>)
    /// or an operator (<c>and</c> in <c>-and</c>).
    /// </summary>
    public static bool References(string? expression, string? symbol)
    {
        if (string.IsNullOrEmpty(expression) || string.IsNullOrWhiteSpace(symbol)) return false;
        for (var at = expression.IndexOf(symbol, StringComparison.Ordinal);
             at >= 0;
             at = expression.IndexOf(symbol, at + 1, StringComparison.Ordinal))
        {
            var before = at > 0 ? expression[at - 1] : ' ';
            var after = at + symbol.Length < expression.Length ? expression[at + symbol.Length] : ' ';
            if (!IsNameChar(before) && before != '-' && !IsNameChar(after)) return true;
        }
        return false;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Every rule over one detect section: what is wrong, and where.</summary>
    public static DetectionCheck Check(DetectSection detect)
    {
        var tests = detect.Tests;
        var expressions = new[] { detect.Expression_Default }
            .Concat(detect.Expressions.Select(e => e.Expression))
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToList();
        var hasTests = tests.Count > 0;

        var duplicates = tests
            .Where(t => !string.IsNullOrEmpty(t.Symbol))
            .GroupBy(t => t.Symbol, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        var symbolMissing = tests.Where(t => string.IsNullOrWhiteSpace(t.Symbol)).ToList();
        var symbolUnused = expressions.Count == 0
            ? new List<DetectionTest>()
            : tests.Where(t => !string.IsNullOrWhiteSpace(t.Symbol) && !expressions.Any(e => References(e, t.Symbol))).ToList();

        return new DetectionCheck(
            DuplicateSymbols: duplicates,
            DefaultExpressionMissing: hasTests && string.IsNullOrWhiteSpace(detect.Expression_Default),
            MissingExpressions: hasTests ? detect.Expressions.Where(e => string.IsNullOrWhiteSpace(e.Expression)).ToList() : new List<ExpressionEntry>(),
            TestsMissing: !hasTests && expressions.Count > 0,
            SymbolMissing: symbolMissing,
            SymbolUnused: symbolUnused);
    }
}

/// <summary>The outcome of <see cref="DetectionRules.Check"/>.</summary>
public sealed record DetectionCheck(
    IReadOnlyList<string> DuplicateSymbols,
    bool DefaultExpressionMissing,
    IReadOnlyList<ExpressionEntry> MissingExpressions,
    bool TestsMissing,
    IReadOnlyList<DetectionTest> SymbolMissing,
    IReadOnlyList<DetectionTest> SymbolUnused)
{
    /// <summary>Blank expressions while tests exist: Default and every named one.</summary>
    public int BlankExpressionCount => (DefaultExpressionMissing ? 1 : 0) + MissingExpressions.Count;

    /// <summary>The symbols no expression uses, each once, in table order.</summary>
    public IReadOnlyList<string> UnusedSymbols =>
        SymbolUnused.Select(t => t.Symbol).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// One per problem, like the Intune and SCCM amber badges: each blank
    /// expression, the missing tests, each test without a symbol, each test
    /// whose symbol is unused. Duplicates are errors and counted apart.
    /// </summary>
    public int WarningCount =>
        BlankExpressionCount + (TestsMissing ? 1 : 0) + SymbolMissing.Count + SymbolUnused.Count;
}
