using Wrapp.Helpers;
using Wrapp.Models;

namespace Wrapp.Tests;

/// <summary>
/// Characterization + dedup guard for the http(s)-URL validity check. The
/// inline <c>IsInformationURLInvalid</c> / <c>IsPrivacyURLInvalid</c> props on
/// <see cref="IntunePackageEntry"/> (which drive the red-field UI indicators)
/// duplicated <see cref="FieldValidators"/>'s URL logic with a confusing
/// non-short-circuit operator. These tests pin the exact truth table so the
/// consolidation onto <c>FieldValidators.IsHttpUrlInvalid</c> can't change a
/// single result.
/// </summary>
public class UrlValidationTests
{
    [Theory]
    [InlineData("",                         false)] // empty is acceptable (required is separate)
    [InlineData("   ",                       false)] // whitespace == empty
    [InlineData("http://example.com",        false)]
    [InlineData("https://example.com/info",  false)]
    [InlineData("HTTPS://Example.COM",       false)] // scheme compare is case-insensitive in Uri
    [InlineData("ftp://example.com",         true)]  // wrong scheme
    [InlineData("file:///c:/x",              true)]  // wrong scheme
    [InlineData("example.com",               true)]  // not absolute (no scheme)
    [InlineData("not a url",                 true)]
    public void IntunePackageEntry_InformationUrlInvalid_MatchesTruthTable(string url, bool expectedInvalid)
    {
        var pkg = new IntunePackageEntry { InformationURL = url };
        Assert.Equal(expectedInvalid, pkg.IsInformationURLInvalid);
    }

    [Theory]
    [InlineData("",                         false)]
    [InlineData("https://privacy.example",   false)]
    [InlineData("ftp://example.com",         true)]
    [InlineData("garbage",                   true)]
    public void IntunePackageEntry_PrivacyUrlInvalid_MatchesTruthTable(string url, bool expectedInvalid)
    {
        var pkg = new IntunePackageEntry { PrivacyURL = url };
        Assert.Equal(expectedInvalid, pkg.IsPrivacyURLInvalid);
    }
}
