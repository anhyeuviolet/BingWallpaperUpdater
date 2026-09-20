using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Ports;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace BingWallpaperUpdater.Windows.Wallpaper;

/// <summary>
/// Applies a wallpaper through <c>IDesktopWallpaper</c> (Windows 8+): <c>SetPosition(DWPOS_FILL)</c> first,
/// then <c>SetWallpaper(NULL, path)</c> — a NULL monitor ID means every monitor (WALL-02). The result is read
/// back with <c>GetWallpaper(NULL)</c>/<c>GetPosition</c> and a mismatch is logged, never thrown (PITFALLS P4).
/// Call on the WinForms UI thread; the COM proxy is created per apply and simply dropped afterwards
/// (source-generated <c>ComObject</c> RCWs must not go through the Marshal release helpers).
/// When activation itself throws, the call degrades to <see cref="SpiWallpaperFallback"/>; a failure
/// after a successful activation (bad path, shell error) is reported as-is without falling back.
/// Phase 1 deliberately has no per-monitor enumeration.
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed unsafe class DesktopWallpaperApplier : IWallpaperApplier
{
    private const string MethodName = "com";

    public ApplyResult Apply(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) || !Path.IsPathRooted(absolutePath) || !File.Exists(absolutePath))
        {
            return new ApplyResult(false, MethodName, null, null, "path not found");
        }

        IDesktopWallpaper wallpaper;
        try
        {
            wallpaper = DesktopWallpaper.CreateInstance<IDesktopWallpaper>();
        }
        catch (COMException ex)
        {
            // Activation failure only (no IDesktopWallpaper on this session): degrade to the legacy SPI path.
            Log.Warn($"apply failed method={MethodName} error=activation failed 0x{ex.HResult:X8} {ex.Message}");
            return SpiWallpaperFallback.Apply(absolutePath);
        }

        try
        {
            wallpaper.SetPosition(DESKTOP_WALLPAPER_POSITION.DWPOS_FILL);
            fixed (char* p = absolutePath)
            {
                wallpaper.SetWallpaper(default, new PCWSTR(p));
            }

            string? readBack = ReadBack(wallpaper);
            DESKTOP_WALLPAPER_POSITION position;
            wallpaper.GetPosition(&position);

            if (readBack is null || !string.Equals(readBack, absolutePath, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"apply readback-mismatch expected={absolutePath} actual={readBack ?? "-"}");
            }

            return new ApplyResult(true, MethodName, readBack, position.ToString(), null);
        }
        catch (COMException ex)
        {
            return new ApplyResult(false, MethodName, null, null, $"0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private static string? ReadBack(IDesktopWallpaper wallpaper)
    {
        PWSTR value = default;
        wallpaper.GetWallpaper(default, &value);
        if (value.Value is null)
        {
            return null;
        }

        try
        {
            return value.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)value.Value);
        }
    }
}
