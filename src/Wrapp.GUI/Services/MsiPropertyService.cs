using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Wrapp.Services;

/// <summary>
/// Reads properties and icons from MSI/MSP files via the Windows Installer API (msi.dll).
/// Uses P/Invoke directly -- no additional NuGet packages required.
/// </summary>
public static class MsiPropertyService
{
    private const int MSIDBOPEN_READONLY = 0;
    // Required for .msp files
    private const int MSIDBOPEN_PATCHFILE = 32;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint MsiOpenDatabase(string databasePath, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint bufferSize);

    [DllImport("msi.dll")]
    private static extern uint MsiRecordReadStream(IntPtr record, uint field, byte[]? buffer, ref uint bufferSize);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);

    /// <summary>
    /// Reads a single property value from the MSI Property table.
    /// Returns null if the property is not found or reading fails.
    /// </summary>
    public static string? GetProperty(string msiPath, string propertyName)
    {
        IntPtr dbHandle = IntPtr.Zero;
        IntPtr viewHandle = IntPtr.Zero;
        IntPtr recordHandle = IntPtr.Zero;

        try
        {
            uint result = MsiOpenDatabase(msiPath, (IntPtr)MSIDBOPEN_READONLY, out dbHandle);
            if (result != 0) return null;

            string query = $"SELECT `Value` FROM `Property` WHERE `Property` = '{propertyName}'";
            result = MsiDatabaseOpenView(dbHandle, query, out viewHandle);
            if (result != 0) return null;

            result = MsiViewExecute(viewHandle, IntPtr.Zero);
            if (result != 0) return null;

            result = MsiViewFetch(viewHandle, out recordHandle);
            if (result != 0) return null;

            uint bufSize = 512;
            var sb = new StringBuilder((int)bufSize);
            result = MsiRecordGetString(recordHandle, 1, sb, ref bufSize);
            if (result != 0) return null;

            return sb.ToString();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (recordHandle != IntPtr.Zero) MsiCloseHandle(recordHandle);
            if (viewHandle != IntPtr.Zero) MsiCloseHandle(viewHandle);
            if (dbHandle != IntPtr.Zero) MsiCloseHandle(dbHandle);
        }
    }

    /// <summary>
    /// Reads ProductName, Manufacturer, and ProductVersion from an MSI file.
    /// </summary>
    public static (string? ProductName, string? Manufacturer, string? ProductVersion) GetMsiMetadata(string msiPath)
    {
        return (
            GetProperty(msiPath, "ProductName"),
            GetProperty(msiPath, "Manufacturer"),
            GetProperty(msiPath, "ProductVersion")
        );
    }

    /// <summary>
    /// Reads metadata from an MSP (patch) file via the MsiPatchMetadata table.
    /// MSP files use different property names than MSI: DisplayName, ManufacturerName.
    /// Version is not typically available in patch metadata.
    /// </summary>
    public static (string? DisplayName, string? Manufacturer, string? TargetProductName) GetMspMetadata(string mspPath)
    {
        return (
            GetPatchProperty(mspPath, "DisplayName"),
            GetPatchProperty(mspPath, "ManufacturerName"),
            GetPatchProperty(mspPath, "TargetProductName")
        );
    }

    /// <summary>
    /// Reads a single property value from the MSP MsiPatchMetadata table.
    /// Returns null if the property is not found or reading fails.
    /// </summary>
    public static string? GetPatchProperty(string mspPath, string propertyName)
    {
        IntPtr dbHandle = IntPtr.Zero;
        IntPtr viewHandle = IntPtr.Zero;
        IntPtr recordHandle = IntPtr.Zero;

        try
        {
            uint result = MsiOpenDatabase(mspPath, (IntPtr)MSIDBOPEN_PATCHFILE, out dbHandle);
            if (result != 0) return null;

            string query = $"SELECT `Value` FROM `MsiPatchMetadata` WHERE `Property` = '{propertyName}'";
            result = MsiDatabaseOpenView(dbHandle, query, out viewHandle);
            if (result != 0) return null;

            result = MsiViewExecute(viewHandle, IntPtr.Zero);
            if (result != 0) return null;

            result = MsiViewFetch(viewHandle, out recordHandle);
            if (result != 0) return null;

            uint bufSize = 512;
            var sb = new StringBuilder((int)bufSize);
            result = MsiRecordGetString(recordHandle, 1, sb, ref bufSize);
            if (result != 0) return null;

            return sb.ToString();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (recordHandle != IntPtr.Zero) MsiCloseHandle(recordHandle);
            if (viewHandle != IntPtr.Zero) MsiCloseHandle(viewHandle);
            if (dbHandle != IntPtr.Zero) MsiCloseHandle(dbHandle);
        }
    }

    /// <summary>
    /// Reads all icons from the MSI Icon table and returns them as BitmapSource images.
    /// Returns an empty list if the Icon table does not exist or cannot be read.
    /// </summary>
    public static List<(string Name, BitmapSource Icon)> GetIcons(string msiPath)
    {
        var icons = new List<(string Name, BitmapSource Icon)>();

        IntPtr dbHandle = IntPtr.Zero;
        IntPtr viewHandle = IntPtr.Zero;

        try
        {
            uint result = MsiOpenDatabase(msiPath, (IntPtr)MSIDBOPEN_READONLY, out dbHandle);
            if (result != 0) return icons;

            result = MsiDatabaseOpenView(dbHandle, "SELECT `Name`, `Data` FROM `Icon`", out viewHandle);
            if (result != 0) return icons;

            result = MsiViewExecute(viewHandle, IntPtr.Zero);
            if (result != 0) return icons;

            while (true)
            {
                IntPtr recordHandle = IntPtr.Zero;
                try
                {
                    result = MsiViewFetch(viewHandle, out recordHandle);
                    if (result != 0) break; // No more records

                    // Read icon name (field 1)
                    uint nameSize = 512;
                    var nameSb = new StringBuilder((int)nameSize);
                    if (MsiRecordGetString(recordHandle, 1, nameSb, ref nameSize) != 0)
                        continue;
                    var name = nameSb.ToString();

                    // Get stream size (field 2) by passing null buffer
                    uint streamSize = 0;
                    MsiRecordReadStream(recordHandle, 2, null, ref streamSize);
                    if (streamSize == 0) continue;

                    var buffer = new byte[streamSize];
                    uint readSize = streamSize;
                    if (MsiRecordReadStream(recordHandle, 2, buffer, ref readSize) != 0)
                        continue;

                    var bmp = ConvertIcoToBitmapSource(buffer);
                    if (bmp is not null)
                        icons.Add((name, bmp));
                }
                catch
                {
                    // Skip icons that cannot be read
                }
                finally
                {
                    if (recordHandle != IntPtr.Zero) MsiCloseHandle(recordHandle);
                }
            }
        }
        catch
        {
            // Icon table may not exist -- return whatever we have
        }
        finally
        {
            if (viewHandle != IntPtr.Zero) MsiCloseHandle(viewHandle);
            if (dbHandle != IntPtr.Zero) MsiCloseHandle(dbHandle);
        }

        return icons;
    }

    /// <summary>
    /// Converts raw ICO/PNG/BMP bytes from the MSI Icon table into a frozen BitmapSource.
    /// Picks the largest frame if the ICO contains multiple resolutions.
    /// </summary>
    private static BitmapSource? ConvertIcoToBitmapSource(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            BitmapFrame best = decoder.Frames[0];
            foreach (var frame in decoder.Frames)
            {
                if (frame.PixelWidth * frame.PixelHeight > best.PixelWidth * best.PixelHeight)
                    best = frame;
            }

            best.Freeze();
            return best;
        }
        catch
        {
            return null;
        }
    }
}
