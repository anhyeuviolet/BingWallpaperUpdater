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
/// When activation itself throws (any type <see cref="ComFailure.IsActivationFailure"/> admits), the call degrades
/// to <see cref="SpiWallpaperFallback"/>; a failure after a successful activation (bad path, shell error — any type
/// <see cref="ComFailure.IsCallFailure"/> admits) is reported as an <see cref="ApplyResult"/> without falling back.
/// Per-monitor mode (WALL-03, Phase 3): <see cref="GetAttachedMonitors"/> enumerates
/// <c>GetMonitorDevicePathCount</c>/<c>At</c> and keeps only the entries <c>GetMonitorRECT</c> succeeds for (the
/// count also includes detached monitors that still have an image assigned — 4 entries for 2 monitors on the
/// research desk); <see cref="ApplyPerMonitor"/> sets one path per attached monitor and reads back PER monitor,
/// because <c>GetWallpaper(NULL)</c> is empty whenever monitors differ (03-RESEARCH Pitfall 8).
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed unsafe class DesktopWallpaperApplier : IWallpaperApplier
{
    private const string MethodName = "com";

    public ApplyResult Apply(string absolutePath)
    {
        if (!IsUsablePath(absolutePath))
        {
            return new ApplyResult(false, MethodName, null, null, "path not found");
        }

        IDesktopWallpaper wallpaper;
        try
        {
            wallpaper = DesktopWallpaper.CreateInstance<IDesktopWallpaper>();
        }
        catch (Exception ex) when (ComFailure.IsActivationFailure(ex))
        {
            // Activation failure only (no IDesktopWallpaper on this session): degrade to the legacy SPI path.
            // CreateInstance goes through Marshal.ThrowExceptionForHR, which maps HRESULTs to several types
            // (COMException, InvalidCastException, NotImplementedException, UnauthorizedAccessException, ...).
            Log.Warn($"apply failed method={MethodName} error=activation failed {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return SpiWallpaperFallback.Apply(absolutePath);
        }

        try
        {
            wallpaper.SetPosition(DESKTOP_WALLPAPER_POSITION.DWPOS_FILL);
            fixed (char* p = absolutePath)
            {
                wallpaper.SetWallpaper(default, new PCWSTR(p));
            }

            string? readBack = ReadBack(wallpaper, default);
            DESKTOP_WALLPAPER_POSITION position;
            wallpaper.GetPosition(&position);

            if (readBack is null || !string.Equals(readBack, absolutePath, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"apply readback-mismatch expected={absolutePath} actual={readBack ?? "-"}");
            }

            return new ApplyResult(true, MethodName, readBack, position.ToString(), null);
        }
        catch (Exception ex) when (ComFailure.IsCallFailure(ex))
        {
            // SetPosition / SetWallpaper / GetWallpaper failed after a good activation: E_INVALIDARG surfaces as
            // ArgumentException and E_ACCESSDENIED as UnauthorizedAccessException, not only as COMException.
            return new ApplyResult(false, MethodName, null, null, $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MonitorHandle> GetAttachedMonitors()
    {
        IDesktopWallpaper wallpaper;
        try
        {
            wallpaper = DesktopWallpaper.CreateInstance<IDesktopWallpaper>();
        }
        catch (Exception ex) when (ComFailure.IsActivationFailure(ex))
        {
            // No IDesktopWallpaper on this session: the SPI fallback has no per-monitor support, so report none.
            Log.Warn($"monitors unavailable error=activation failed {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return [];
        }

        var attached = new List<MonitorHandle>();
        uint count = 0;
        try
        {
            wallpaper.GetMonitorDevicePathCount(out count);
            for (uint i = 0; i < count; i++)
            {
                string? devicePath = DevicePathAt(wallpaper, i);
                if (devicePath is null)
                {
                    continue;
                }

                RECT rect;
                try
                {
                    fixed (char* m = devicePath)
                    {
                        wallpaper.GetMonitorRECT(new PCWSTR(m), &rect);
                    }
                }
                catch (Exception ex) when (ComFailure.IsCallFailure(ex))
                {
                    continue;   // detached entry that still has an image assigned (0x80004005 observed) — Pitfall 2
                }

                attached.Add(new MonitorHandle(devicePath, rect.right - rect.left, rect.bottom - rect.top, rect.left, rect.top));
            }
        }
        catch (Exception ex) when (ComFailure.IsCallFailure(ex))
        {
            Log.Warn($"monitors unavailable error={ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return [];
        }

        Log.Info($"monitors attached={attached.Count} total={count}");
        return attached;
    }

    /// <inheritdoc />
    public ApplyResult ApplyPerMonitor(IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0)
        {
            return new ApplyResult(false, MethodName, null, null, "no assignments");
        }

        foreach ((MonitorHandle _, string absolutePath) in assignments)
        {
            if (!IsUsablePath(absolutePath))
            {
                return new ApplyResult(false, MethodName, null, null, "path not found");
            }
        }

        IDesktopWallpaper wallpaper;
        try
        {
            wallpaper = DesktopWallpaper.CreateInstance<IDesktopWallpaper>();
        }
        catch (Exception ex) when (ComFailure.IsActivationFailure(ex))
        {
            Log.Warn($"apply failed method={MethodName} error=activation failed {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return SpiWallpaperFallback.Apply(assignments[0].AbsolutePath);
        }

        try
        {
            wallpaper.SetPosition(DESKTOP_WALLPAPER_POSITION.DWPOS_FILL);
            foreach ((MonitorHandle monitor, string absolutePath) in assignments)
            {
                fixed (char* m = monitor.DevicePath)
                fixed (char* p = absolutePath)
                {
                    wallpaper.SetWallpaper(new PCWSTR(m), new PCWSTR(p));
                }
            }

            // Read back PER monitor — never GetWallpaper(NULL) here: it answers "" as soon as monitors differ.
            string? primaryReadBack = null;
            for (int i = 0; i < assignments.Count; i++)
            {
                (MonitorHandle monitor, string absolutePath) = assignments[i];
                string? readBack;
                fixed (char* m = monitor.DevicePath)
                {
                    readBack = ReadBack(wallpaper, new PCWSTR(m));
                }

                if (i == 0)
                {
                    primaryReadBack = readBack;
                }

                if (readBack is null || !string.Equals(readBack, absolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"apply readback-mismatch monitor={i} expected={absolutePath} actual={readBack ?? "-"}");
                }
            }

            DESKTOP_WALLPAPER_POSITION position;
            wallpaper.GetPosition(&position);
            return new ApplyResult(true, MethodName, primaryReadBack, position.ToString(), null);
        }
        catch (Exception ex) when (ComFailure.IsCallFailure(ex))
        {
            return new ApplyResult(false, MethodName, null, null, $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private static bool IsUsablePath(string? absolutePath) =>
        !string.IsNullOrEmpty(absolutePath) && Path.IsPathRooted(absolutePath) && File.Exists(absolutePath);

    /// <summary>The device path at <paramref name="index"/>; the shell's CoTaskMem string is freed once copied.</summary>
    private static string? DevicePathAt(IDesktopWallpaper wallpaper, uint index)
    {
        PWSTR id = default;
        wallpaper.GetMonitorDevicePathAt(index, &id);
        if (id.Value is null)
        {
            return null;
        }

        try
        {
            return id.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)id.Value);
        }
    }

    /// <summary>What the shell reports for <paramref name="monitorId"/> (<c>default</c> = the NULL monitor, same-image mode only).</summary>
    private static string? ReadBack(IDesktopWallpaper wallpaper, PCWSTR monitorId)
    {
        PWSTR value = default;
        wallpaper.GetWallpaper(monitorId, &value);
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
