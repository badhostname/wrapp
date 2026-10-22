using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// The Detection view's consistency rules. "Used" must mean what the detect
/// script means: a case-sensitive, whole-name reference (the script
/// substitutes symbols with a case-sensitive Replace and runs only the tests
/// it finds in the selected expression).
/// </summary>
public class DetectionRulesTests
{
    private static DetectionTest Test(string symbol) => new() { Name = symbol, Symbol = symbol, Operator = "-eq", Value = "True" };

    private static DetectSection Section(string defaultExpression, params string[] symbols)
    {
        var detect = new DetectSection { Expression_Default = defaultExpression };
        foreach (var s in symbols) detect.Tests.Add(Test(s));
        return detect;
    }

    [Theory]
    [InlineData("A", "A", true)]
    [InlineData("A -and B", "B", true)]
    [InlineData("A -and (B -or -not C)", "C", true)]
    [InlineData("!A", "A", true)]
    [InlineData("(Base)", "Base", true)]
    [InlineData("BaseV2 -or Base", "Base", true)]   // the standalone one counts
    [InlineData("Base -and Fake", "B", false)]     // inside a longer symbol
    [InlineData("AB -and C", "A", false)]
    [InlineData("A_1 -and C", "A", false)]
    [InlineData("A -and B", "and", false)]         // part of an operator, not a symbol
    [InlineData("a -and B", "A", false)]           // case-sensitive, like the script
    [InlineData("", "A", false)]
    [InlineData("A -and B", "", false)]
    public void A_Symbol_Is_Referenced_Only_As_A_Whole_Name(string expression, string symbol, bool expected)
        => Assert.Equal(expected, DetectionRules.References(expression, symbol));

    [Fact]
    public void A_New_Bundle_Has_Nothing_To_Warn_About()
    {
        var check = DetectionRules.Check(new DetectSection());
        Assert.Equal(0, check.WarningCount);
        Assert.Empty(check.DuplicateSymbols);
        Assert.False(check.TestsMissing);
    }

    [Fact]
    public void Tests_With_A_Blank_Expression_Warn_On_Each_Blank_Expression()
    {
        var detect = Section("", "A");
        detect.Expressions.Add(new ExpressionEntry { Key = "Visio", Expression = "A" });
        detect.Expressions.Add(new ExpressionEntry { Key = "Project", Expression = "  " });

        var check = DetectionRules.Check(detect);

        Assert.True(check.DefaultExpressionMissing);
        Assert.Equal("Project", Assert.Single(check.MissingExpressions).Key);
        Assert.Equal(2, check.BlankExpressionCount);
        Assert.Equal(2, check.WarningCount);
    }

    [Fact]
    public void Expressions_Without_Tests_Warn_Once()
    {
        var check = DetectionRules.Check(Section("A -and B"));
        Assert.True(check.TestsMissing);
        Assert.False(check.DefaultExpressionMissing);
        Assert.Equal(1, check.WarningCount);
    }

    [Fact]
    public void A_Test_Without_A_Symbol_Is_Missing_Not_Unused()
    {
        var detect = Section("A", "A");
        detect.Tests.Add(Test(""));

        var check = DetectionRules.Check(detect);

        Assert.Single(check.SymbolMissing);
        Assert.Empty(check.SymbolUnused);
        Assert.Equal(1, check.WarningCount);
    }

    [Fact]
    public void A_Symbol_Used_In_Any_Expression_Is_Used()
    {
        var detect = Section("A", "A", "B", "C");
        detect.Expressions.Add(new ExpressionEntry { Key = "Visio", Expression = "A -and B" });

        var check = DetectionRules.Check(detect);

        Assert.Equal(new[] { "C" }, check.UnusedSymbols);
        Assert.Equal(1, check.WarningCount);
    }

    [Fact]
    public void Unused_Symbols_Are_Checked_Only_Once_An_Expression_Has_Content()
    {
        // With every expression blank, the blank-expression warning already says it:
        // flagging every test as unused too would bury it.
        var check = DetectionRules.Check(Section("", "A", "B"));
        Assert.Empty(check.SymbolUnused);
        Assert.Equal(1, check.WarningCount);
    }

    [Fact]
    public void Duplicate_Symbols_Are_Errors_Not_Warnings()
    {
        var check = DetectionRules.Check(Section("A", "A", "A"));
        Assert.Equal(new[] { "A" }, check.DuplicateSymbols);
        Assert.Equal(0, check.WarningCount);
    }
}
