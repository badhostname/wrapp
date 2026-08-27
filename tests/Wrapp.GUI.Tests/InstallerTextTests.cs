using System.IO;

namespace Wrapp.Tests;

/// <summary>
/// The MSI wizard's welcome and conclusion text land in FIXED-SIZE MSI Text
/// controls. Overflow is not scrolled — it is clipped with an ellipsis, which
/// is exactly how 0.6.309 shipped a half-sentence on the finish page. The
/// control geometry comes from Velopack's WiX templates (dialog units):
/// <list type="bullet">
/// <item><description>WelcomeDlg Description — 220x150 DU, ~12 lines</description></item>
/// <item><description>ExitDialog OptionalText — 220x80 DU, ~6 lines</description></item>
/// </list>
/// A line holds roughly 55-60 characters at the default 8pt font. These tests
/// approximate the wrap so an edit that overflows fails here instead of in a
/// screenshot after a 10-minute pack.
/// </summary>
public class InstallerTextTests
{
    private const int CharsPerLine = 58;

    private static string AssetPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Wrapp.GUI", "Assets", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        throw new FileNotFoundException($"{fileName} not found walking up from the test base directory");
    }

    /// <summary>Blank lines count as one; text lines wrap at <see cref="CharsPerLine"/>.</summary>
    private static int EstimateDisplayLines(string text)
    {
        var lines = 0;
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            lines += paragraph.Trim().Length == 0
                ? 1
                : (int)Math.Ceiling(paragraph.Length / (double)CharsPerLine);
        }
        return lines;
    }

    [Theory]
    [InlineData("installer-welcome.txt", 12)]
    [InlineData("installer-conclusion.txt", 6)]
    public void WizardText_FitsItsControl(string fileName, int capacityLines)
    {
        var text = File.ReadAllText(AssetPath(fileName)).TrimEnd();
        var estimated = EstimateDisplayLines(text);
        Assert.True(estimated <= capacityLines,
            $"{fileName} needs ~{estimated} display lines but the MSI control fits ~{capacityLines}; " +
            "the overflow is clipped with an ellipsis in the installer. Shorten the text.");
    }
}
