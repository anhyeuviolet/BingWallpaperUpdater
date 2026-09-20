using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Ports;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace BingWallpaperUpdater.Windows.Wallpaper;

/// <summary>
/// Legacy <c>SystemParametersInfo(SPI_SETDESKWALLPAPER)</c> path, used ONLY when <c>IDesktopWallpaper</c>
/// activation throws. It sets one image on all monitors; Fill is expressed through the two HKCU values
/// <c>WallpaperStyle=10</c> / <c>TileWallpaper=0</c>, which must be written before the call (CLAUDE.md
/// "Wallpaper API"). Those two values are the only registry writes in Phase 1. The BOOL result is never
/// trusted on its own — the API returns TRUE and paints the desktop black for bad input (PITFALLS P4) —
/// so the result is read back with <c>SPI_GETDESKWALLPAPER</c> and compared to the requested path.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal static unsafe class SpiWallpaperFallback
{
    private const string MethodName = "spi";
    private const string DesktopKey = @"Control Panel\Desktop";
    private const int MaxPathChars = 260;

    public static ApplyResult Apply(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) || !Path.IsPathRooted(absolutePath) || !File.Exists(absolutePath))
        {
            return new ApplyResult(false, MethodName, null, null, "path not found");
        }

        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(DesktopKey, writable: true))
            {
                if (key is null)
                {
                    return new ApplyResult(false, MethodName, null, null, $@"HKCU\{DesktopKey} not found");
                }

                key.SetValue("WallpaperStyle", "10", RegistryValueKind.String); // 10 = Fill
                key.SetValue("TileWallpaper", "0", RegistryValueKind.String);
            }

            bool set;
            fixed (char* p = absolutePath)
            {
                set = PInvoke.SystemParametersInfo(
                    SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETDESKWALLPAPER,
                    0,
                    p,
                    SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS.SPIF_UPDATEINIFILE | SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS.SPIF_SENDCHANGE);
            }

            string? readBack = ReadBack();
            bool matches = readBack is not null && string.Equals(readBack, absolutePath, StringComparison.OrdinalIgnoreCase);
            string? error = matches
                ? null
                : $"set returned {(set ? "TRUE" : "FALSE")} but read-back is '{readBack ?? "-"}'";
            return new ApplyResult(matches, MethodName, readBack, null, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ApplyResult(false, MethodName, null, null, ex.Message);
        }
    }

    private static string? ReadBack()
    {
        char* buffer = stackalloc char[MaxPathChars];
        bool ok = PInvoke.SystemParametersInfo(
            SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETDESKWALLPAPER,
            MaxPathChars,
            buffer,
            0);
        if (!ok)
        {
            return null;
        }

        int length = 0;
        while (length < MaxPathChars && buffer[length] != '\0')
        {
            length++;
        }

        return new string(buffer, 0, length);
    }
}
