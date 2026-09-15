using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// Edge-case coverage on top of the original bounds / guid / required /
/// ISO-date / URL / combobox tests. The `_Edge` suffix marks tests added
/// in the Tier-1 expansion so they're easy to find when diffing.
/// </summary>
public class FieldValidatorsTests
{
    [Theory]
    [InlineData("123", null)]
    [InlineData("0", "Must be at least 1")]
    [InlineData("9999", "Must be at most 1440")]
    [InlineData("abc", "Must be a whole number")]
    [InlineData("", null)]
    public void Int_RespectsBounds(string input, string? expectedError)
    {
        var d = new FieldDescriptor("X", FieldKind.Int, Min: 1, Max: 1440);
        Assert.Equal(expectedError, FieldValidators.Validate(d, input));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("not-a-guid", "Must be a valid GUID")]
    [InlineData("", null)]
    public void Guid_AcceptsRealGuid(string input, string? expectedError)
    {
        var d = new FieldDescriptor("X", FieldKind.Guid);
        Assert.Equal(expectedError, FieldValidators.Validate(d, input));
    }

    [Fact]
    public void Required_BlocksEmpty()
    {
        var d = new FieldDescriptor("X", FieldKind.String, Required: true);
        Assert.Equal("Required", FieldValidators.Validate(d, ""));
        Assert.Null(FieldValidators.Validate(d, "value"));
    }

    [Theory]
    [InlineData("2025-01-01T00:00:00Z", null)]
    [InlineData("not-a-date", "Must be a valid ISO-8601 date/time")]
    public void IsoDate_AcceptsRoundtrip(string input, string? expectedError)
    {
        var d = new FieldDescriptor("X", FieldKind.IsoDate);
        Assert.Equal(expectedError, FieldValidators.Validate(d, input));
    }

    [Theory]
    [InlineData("https://example.com", null)]
    [InlineData("ftp://example.com", "Must be a valid http(s) URL")]
    [InlineData("not a url", "Must be a valid http(s) URL")]
    public void Url_RequiresHttpScheme(string input, string? expectedError)
    {
        var d = new FieldDescriptor("X", FieldKind.Url);
        Assert.Equal(expectedError, FieldValidators.Validate(d, input));
    }

    [Fact]
    public void ComboBox_RejectsValueOutsideAllowed()
    {
        var d = new FieldDescriptor("X", FieldKind.ComboBox,
            AllowedValues: ["available", "required", "uninstall"]);
        Assert.Null(FieldValidators.Validate(d, "Required"));
        Assert.NotNull(FieldValidators.Validate(d, "wat"));
    }

    // ── Edge cases added in Tier-1 test expansion ───────────────────

    [Theory]
    [InlineData(1, 1440, 1, null)]                      // at min -> ok
    [InlineData(1, 1440, 1440, null)]                   // at max -> ok
    [InlineData(1, 1440, 2, null)]                      // inside bounds -> ok
    [InlineData(1, 1440, 0, "Must be at least 1")]      // one below -> reject
    [InlineData(1, 1440, 1441, "Must be at most 1440")] // one above -> reject
    [InlineData(-5, 5, -5, null)]                       // negative bounds ok
    [InlineData(-5, 5, -6, "Must be at least -5")]      // below negative min
    public void Int_BoundaryValues_Edge(int min, int max, int input, string? expected)
    {
        var d = new FieldDescriptor("X", FieldKind.Int, Min: min, Max: max);
        Assert.Equal(expected, FieldValidators.Validate(d, input.ToString()));
    }

    [Theory]
    [InlineData("2147483647", null)]                // int.MaxValue fits
    [InlineData("2147483648", "Must be a whole number")] // overflow -> TryParse fails
    [InlineData("1.5", "Must be a whole number")]   // decimals rejected
    [InlineData(" 5 ", null)]                       // leading/trailing whitespace OK (Validate trims)
    public void Int_NumericEdgeCases_Edge(string input, string? expected)
    {
        var d = new FieldDescriptor("X", FieldKind.Int);
        Assert.Equal(expected, FieldValidators.Validate(d, input));
    }

    [Theory]
    [InlineData("",                       null)]                      // empty ok (required handled elsewhere)
    [InlineData("not-a-guid",             "Must be a valid GUID")]
    [InlineData("00000000-0000-0000-0000",               "Must be a valid GUID")] // truncated
    [InlineData("{00000000-0000-0000-0000-000000000000}", null)]      // braces form
    [InlineData("00000000000000000000000000000000",      null)]       // no-hyphen form
    public void Guid_VariousForms_Edge(string input, string? expected)
    {
        var d = new FieldDescriptor("X", FieldKind.Guid);
        Assert.Equal(expected, FieldValidators.Validate(d, input));
    }

    [Theory]
    [InlineData("http://example.com",        null)]
    [InlineData("https://example.com",       null)]
    [InlineData("http://example.com/path?q", null)]
    [InlineData("file:///C:/thing",          "Must be a valid http(s) URL")] // file:// rejected
    [InlineData("javascript:alert(1)",       "Must be a valid http(s) URL")] // XSS attempt rejected
    [InlineData("/relative/path",            "Must be a valid http(s) URL")] // relative rejected
    [InlineData("example.com",               "Must be a valid http(s) URL")] // missing scheme
    public void Url_EdgeCasesAndSecurity_Edge(string input, string? expected)
    {
        var d = new FieldDescriptor("X", FieldKind.Url);
        Assert.Equal(expected, FieldValidators.Validate(d, input));
    }

    [Fact]
    public void ComboBox_CaseInsensitiveMatch_Edge()
    {
        var d = new FieldDescriptor("X", FieldKind.ComboBox,
            AllowedValues: ["Available", "Required"]);
        // Match regardless of case (existing production behaviour).
        Assert.Null(FieldValidators.Validate(d, "AVAILABLE"));
        Assert.Null(FieldValidators.Validate(d, "required"));
    }

    [Fact]
    public void ComboBox_EmptyAllowedList_AllowsAnything_Edge()
    {
        // Production convention: empty allowed list = no enum restriction.
        var d = new FieldDescriptor("X", FieldKind.ComboBox, AllowedValues: []);
        Assert.Null(FieldValidators.Validate(d, "anything-goes"));
    }

    [Fact]
    public void ComboBox_SubstringMatch_IsRejected_Edge()
    {
        // "Req" should NOT be accepted when allowed is ["Required"] --
        // the matcher uses equality, not contains.
        var d = new FieldDescriptor("X", FieldKind.ComboBox,
            AllowedValues: ["Required"]);
        Assert.NotNull(FieldValidators.Validate(d, "Req"));
    }

    [Fact]
    public void Validate_NullRawValue_TreatedAsEmpty_Edge()
    {
        // Null input reaches the empty-check path. Required -> reject;
        // optional -> accept.
        var required = new FieldDescriptor("X", FieldKind.String, Required: true);
        var optional = new FieldDescriptor("X", FieldKind.String);
        Assert.Equal("Required", FieldValidators.Validate(required, null));
        Assert.Null(FieldValidators.Validate(optional, null));
    }

    [Fact]
    public void Required_WhitespaceOnly_CountsAsEmpty_Edge()
    {
        // Whitespace-only values should NOT satisfy Required since the
        // validator trims before checking length.
        var d = new FieldDescriptor("X", FieldKind.String, Required: true);
        Assert.Equal("Required", FieldValidators.Validate(d, "   "));
    }

    [Theory]
    [InlineData("2025-04-17T10:30:00-04:00", null)]   // with offset
    [InlineData("2025-04-17",                null)]   // date only (DateTimeOffset accepts)
    [InlineData("17/04/2025",                "Must be a valid ISO-8601 date/time")] // non-ISO
    public void IsoDate_FormatsAndNonFormats_Edge(string input, string? expected)
    {
        var d = new FieldDescriptor("X", FieldKind.IsoDate);
        Assert.Equal(expected, FieldValidators.Validate(d, input));
    }
}
