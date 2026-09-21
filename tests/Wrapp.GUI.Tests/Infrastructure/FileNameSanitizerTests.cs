using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Smoke tests for the <see cref="FileNameSanitizer"/> primitive that
/// consolidated duplicated <c>SanitizeName</c> / <c>SanitizeFileName</c>
/// implementations from <c>EncryptionKeyStoreService</c> and
/// <c>InventoryViewModel</c>.
/// </summary>
public class FileNameSanitizerTests
{
    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FileNameSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, FileNameSanitizer.Sanitize(""));
    }

    [Fact]
    public void Sanitize_PlainAsciiName_PassesThrough()
    {
        Assert.Equal("My App", FileNameSanitizer.Sanitize("My App"));
    }

    [Fact]
    public void Sanitize_InvalidChars_ReplacedWithUnderscore()
    {
        // Cover the common Windows-invalid set: < > : " / \ | ? *
        Assert.Equal("a_b_c_d", FileNameSanitizer.Sanitize("a:b/c\\d"));
        Assert.Equal("App_Beta_", FileNameSanitizer.Sanitize("App<Beta>"));
        Assert.Equal("Q_A", FileNameSanitizer.Sanitize("Q?A"));
    }

    [Fact]
    public void Sanitize_SurroundingWhitespace_Trimmed()
    {
        Assert.Equal("name", FileNameSanitizer.Sanitize("  name  "));
    }

    [Fact]
    public void Sanitize_PathSeparators_StrippedNotPreserved()
    {
        // Documented contract: path separators are sanitised away. Callers
        // who want directory-aware sanitisation use BundleService.Sanitize.
        var result = FileNameSanitizer.Sanitize("Foo\\Bar/Baz");
        Assert.DoesNotContain("\\", result);
        Assert.DoesNotContain("/", result);
    }
}
