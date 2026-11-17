using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wrapp.Helpers;
using Wrapp.Models;
using Wrapp.ViewModels;

namespace Wrapp.Tests;

/// <summary>
/// The icon image is not in Config.json (only its file name), so replacing it
/// with one of the same name used to leave the bundle "Up to date": Extract
/// Icon Only on Snagit, whose Config.json already said Icon\Snagit.png. The
/// dirty check now compares the icon's pixels with the ones last opened or saved.
/// </summary>
public class IconDirtyStateTests
{
    private static void Sta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static BitmapSource Solid(byte r, byte g, byte b, int size = 16)
    {
        var px = new byte[size * size * 4];
        for (var i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void Fingerprints_match_for_the_same_picture_and_differ_for_another()
    {
        Sta(() =>
        {
            Assert.Equal(string.Empty, IconFingerprint.Of(null));
            Assert.Equal(IconFingerprint.Of(Solid(10, 20, 30)), IconFingerprint.Of(Solid(10, 20, 30)));
            Assert.NotEqual(IconFingerprint.Of(Solid(10, 20, 30)), IconFingerprint.Of(Solid(10, 20, 31)));
            Assert.NotEqual(IconFingerprint.Of(Solid(10, 20, 30, 16)), IconFingerprint.Of(Solid(10, 20, 30, 32)));
        });
    }

    [Fact]
    public void Replacing_the_icon_marks_the_bundle_unsaved_even_when_Config_json_is_unchanged()
    {
        Sta(() =>
        {
            var vm = new GeneralViewModel(new AppSettings());
            var config = new AppConfigModel();
            config.App.Name = "Snagit";
            config.App.IconFile = @"Icon\Snagit.png";
            vm.ApplyConfig(config, @"C:\Bundles\TechSmith\Snagit\26_4_0\Script\Config.json");
            vm.InstallerIconSource = Solid(200, 80, 40);
            vm.TakeSnapshot();                      // as after opening or saving
            Assert.False(vm.IsDirty);

            // Extract Icon Only: a new picture, same IconFile.
            vm.InstallerIconSource = Solid(30, 120, 220);
            Assert.Equal(@"Icon\Snagit.png", vm.App.IconFile);
            Assert.True(vm.IconChangedSinceSave);
            Assert.True(vm.IsDirty);

            // Putting the same picture back is not a change.
            vm.InstallerIconSource = Solid(200, 80, 40);
            Assert.False(vm.IsDirty);

            // A save makes the new icon the baseline.
            vm.InstallerIconSource = Solid(30, 120, 220);
            Assert.True(vm.IsDirty);
            vm.TakeSnapshot();
            Assert.False(vm.IsDirty);
            Assert.False(vm.IconChangedSinceSave);

            // Removing the icon is a change too.
            vm.InstallerIconSource = null;
            Assert.True(vm.IsDirty);
        });
    }
}
