using System.Windows.Media;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

public class LogEntryTests
{
    [Fact]
    public void ForegroundBrush_Error_ReturnsSalmon()
    {
        var entry = new LogEntry { Content = "ERR", ColorCode = "Error" };
        Assert.Equal(Brushes.Salmon, entry.ForegroundBrush);
    }

    [Fact]
    public void ForegroundBrush_Warning_ReturnsOrange()
    {
        var entry = new LogEntry { Content = "WARN", ColorCode = "Warning" };
        Assert.Equal(Brushes.Orange, entry.ForegroundBrush);
    }

    [Fact]
    public void ForegroundBrush_Info_ReturnsWhite()
    {
        var entry = new LogEntry { Content = "INFO", ColorCode = "Info" };
        Assert.Equal(Brushes.White, entry.ForegroundBrush);
    }

    [Fact]
    public void ForegroundBrush_Verbose_ReturnsLightGray()
    {
        var entry = new LogEntry { Content = "VERB", ColorCode = "Verbose" };
        Assert.Equal(Brushes.LightGray, entry.ForegroundBrush);
    }

    [Fact]
    public void ForegroundBrush_Default_ReturnsGainsboro()
    {
        var entry = new LogEntry { Content = "LOG", ColorCode = "Default" };
        Assert.Equal(Brushes.Gainsboro, entry.ForegroundBrush);
    }

    [Fact]
    public void ForegroundBrush_Unknown_ReturnsGainsboro()
    {
        var entry = new LogEntry { Content = "LOG", ColorCode = "SomeUnknownCode" };
        Assert.Equal(Brushes.Gainsboro, entry.ForegroundBrush);
    }

    [Fact]
    public void Content_DefaultsToEmptyString()
    {
        var entry = new LogEntry();
        Assert.Equal(string.Empty, entry.Content);
    }

    [Fact]
    public void ColorCode_DefaultsToDefault()
    {
        var entry = new LogEntry();
        Assert.Equal("Default", entry.ColorCode);
    }
}
