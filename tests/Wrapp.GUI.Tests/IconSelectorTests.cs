using System.IO;
using MaterialDesignThemes.Wpf;
using Wrapp.Services;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// feature/icon-selector: the generic-icon library and tile rasterizer.
/// Rendering tests run on a dedicated STA thread because RenderTargetBitmap
/// needs one; the library view-model is plain state and needs nothing.
/// </summary>
public class IconSelectorTests
{
    // ------------------------------------------------------------------
    // Library search / paging
    // ------------------------------------------------------------------

    [Fact]
    public void Library_InitialPage_IsCappedAtPageSize()
    {
        var vm = new IconLibraryViewModel();
        Assert.Equal(IconLibraryViewModel.PageSize, vm.Results.Count);
        Assert.True(vm.CanShowMore);
    }

    [Fact]
    public void Library_ShowMore_AppendsNextPage()
    {
        var vm = new IconLibraryViewModel();
        vm.ShowMoreCommand.Execute(null);
        Assert.Equal(IconLibraryViewModel.PageSize * 2, vm.Results.Count);
    }

    [Fact]
    public void Library_Search_FiltersCaseInsensitively_AndResetsPaging()
    {
        var vm = new IconLibraryViewModel();
        vm.ShowMoreCommand.Execute(null);

        vm.SearchText = "chart";
        Assert.True(vm.Results.Count > 0);
        Assert.True(vm.Results.Count <= IconLibraryViewModel.PageSize);
        Assert.All(vm.Results, r => Assert.Contains("chart", r.Name, StringComparison.OrdinalIgnoreCase));

        // Clearing the search restores the full catalogue's first page.
        vm.SearchText = "";
        Assert.Equal(IconLibraryViewModel.PageSize, vm.Results.Count);
    }

    [Fact]
    public void Library_Search_NoMatches_YieldsEmptyAndNoShowMore()
    {
        var vm = new IconLibraryViewModel();
        vm.SearchText = "zz-no-such-glyph-zz";
        Assert.Empty(vm.Results);
        Assert.False(vm.CanShowMore);
        Assert.Equal("No icons match", vm.ResultSummary);
    }

    [Fact]
    public void Library_SearchResets_Selection()
    {
        var vm = new IconLibraryViewModel();
        vm.Selected = vm.Results[0];
        vm.SearchText = "folder";
        Assert.Null(vm.Selected);
    }

    // ------------------------------------------------------------------
    // Custom color sync (hex <-> RGB <-> SelectedColor) — the standalone
    // picker VM, instantiated per target by the library (background/glyph)
    // ------------------------------------------------------------------

    private static ColorPickerViewModel NewPicker()
        => new(IconTileRenderer.Palette[0], IconTileRenderer.Palette);

    [Fact]
    public void Color_HexInput_UpdatesSelectedColorAndRgb()
    {
        var vm = NewPicker();
        vm.CustomHex = "#4C8FBF";
        Assert.Equal("#4C8FBF", vm.SelectedColor);
        Assert.Equal(0x4C, vm.Red);
        Assert.Equal(0x8F, vm.Green);
        Assert.Equal(0xBF, vm.Blue);
        Assert.False(vm.IsCustomHexInvalid);
    }

    [Fact]
    public void Color_ShortHex_Expands()
    {
        var vm = NewPicker();
        vm.CustomHex = "#abc";
        Assert.Equal("#AABBCC", vm.SelectedColor);
    }

    [Fact]
    public void Color_InvalidHex_FlagsWithoutApplying()
    {
        var vm = NewPicker();
        var before = vm.SelectedColor;
        vm.CustomHex = "#zzz";
        Assert.True(vm.IsCustomHexInvalid);
        Assert.Equal(before, vm.SelectedColor);
    }

    [Fact]
    public void Color_RgbInput_ComposesHexAndClamps()
    {
        var vm = NewPicker();
        vm.Red = 300;  // clamped to 255
        vm.Green = 16;
        vm.Blue = 32;
        Assert.Equal("#FF1020", vm.SelectedColor);
        Assert.Equal("#FF1020", vm.CustomHex);
        Assert.Equal(255, vm.Red);
    }

    [Fact]
    public void Color_SwatchSelection_SyncsHexAndRgb()
    {
        var vm = NewPicker();
        vm.SelectedColor = "#333A42";
        Assert.Equal("#333A42", vm.CustomHex);
        Assert.Equal(0x33, vm.Red);
        Assert.Equal(0x3A, vm.Green);
        Assert.Equal(0x42, vm.Blue);
    }

    // ------------------------------------------------------------------
    // HSV spectrum picker math
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1, 1, 255, 0, 0)]      // pure red
    [InlineData(120, 1, 1, 0, 255, 0)]    // pure green
    [InlineData(240, 1, 1, 0, 0, 255)]    // pure blue
    [InlineData(0, 0, 1, 255, 255, 255)]  // white
    [InlineData(0, 0, 0, 0, 0, 0)]        // black
    public void Hsv_ToRgb_KnownValues(double h, double s, double v, byte r, byte g, byte b)
    {
        Assert.Equal((r, g, b), ColorPickerViewModel.HsvToRgb(h, s, v));
    }

    [Fact]
    public void Hsv_RoundTrips_ThroughRgb()
    {
        var (h, s, v) = ColorPickerViewModel.RgbToHsv(0x9A, 0xC9, 0xCF);
        var (r, g, b) = ColorPickerViewModel.HsvToRgb(h, s, v);
        Assert.Equal((0x9A, 0xC9, 0xCF), ((int)r, (int)g, (int)b));
    }

    [Fact]
    public void Hsv_SpectrumChange_UpdatesHexAndRgb()
    {
        var vm = NewPicker();
        vm.Hue = 120; vm.Saturation = 1; vm.Brightness = 1;
        Assert.Equal("#00FF00", vm.SelectedColor);
        Assert.Equal(0, vm.Red);
        Assert.Equal(255, vm.Green);
    }

    [Fact]
    public void Hsv_HexChange_UpdatesSpectrum_AndGrayKeepsHue()
    {
        var vm = NewPicker();
        vm.CustomHex = "#FF0000";
        Assert.Equal(0, vm.Hue, 3);
        Assert.Equal(1, vm.Saturation, 3);

        var hueBefore = vm.Hue;
        vm.CustomHex = "#808080"; // gray: saturation 0, hue undefined
        Assert.Equal(0, vm.Saturation, 3);
        Assert.Equal(hueBefore, vm.Hue, 3); // marker doesn't jump
    }

    // ------------------------------------------------------------------
    // Tile rasterizer (STA)
    // ------------------------------------------------------------------

    [Fact]
    public void Renderer_GlyphData_ResolvesWithoutMergedThemes()
    {
        RunSta(() =>
        {
            var data = IconTileRenderer.GetGlyphData(PackIconKind.Application);
            Assert.False(string.IsNullOrWhiteSpace(data));
        });
    }

    [Fact]
    public void Renderer_ProducesPngAtTileSize()
    {
        RunSta(() =>
        {
            var bytes = IconTileRenderer.RenderPng(PackIconKind.Application, IconTileRenderer.Palette[0]);

            // PNG signature + decoded dimensions.
            Assert.True(bytes.Length > 1000);
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);

            using var ms = new MemoryStream(bytes);
            var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal(IconTileRenderer.TileSize, decoder.Frames[0].PixelWidth);
            Assert.Equal(IconTileRenderer.TileSize, decoder.Frames[0].PixelHeight);
        });
    }

    [Fact]
    public void Renderer_GlyphColor_IsParameterizable()
    {
        RunSta(() =>
        {
            var whiteGlyph = IconTileRenderer.RenderPng(PackIconKind.Application, "#000000");
            var redGlyph   = IconTileRenderer.RenderPng(PackIconKind.Application, "#000000", "#FF0000");
            Assert.NotEqual(whiteGlyph, redGlyph); // different glyph color -> different pixels
        });
    }

    [Fact]
    public void Renderer_TempFile_IsNamedForTheApp_AndSanitized()
    {
        RunSta(() =>
        {
            var path = IconTileRenderer.RenderToTempFile(
                PackIconKind.Application, IconTileRenderer.Palette[0], @"My: App/Name");
            Assert.EndsWith(".png", path);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain(':', Path.GetFileName(path));
            Assert.DoesNotContain('/', Path.GetFileName(path));
            File.Delete(path);
        });
    }

    /// <summary>Runs work on an STA thread, propagating any assertion failure.</summary>
    private static void RunSta(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
