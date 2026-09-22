using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Ports;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace BingWallpaperUpdater.Windows.Wallpaper;

/// <summary>
/// Applies a wallpaper through <c>IDesktopWallpaper</c> (Windows 8+): <c>SetPosition(DWPOS_FILL)</c> first,
/// then <c>SetWallpaper(NULL, path)</c> — a NULL monitor ID means every monitor (WALL-02). The result is read
/// back with <c>GetWallpaper(NULL)</c>/<c>GetPosition</c>; a mismatch is logged, never thrown (PITFALLS P4), and a
/// read-back that itself fails after a successful set is logged and reported as Ok with an unknown read-back, never
/// as a failed apply (IN-14) — only <c>SetPosition</c>/<c>SetWallpaper</c> decide <see cref="ApplyResult.Ok"/>.
/// Call on the WinForms UI thread; one COM proxy is created per operation and released deterministically in a
/// <c>finally</c> on the same STA thread (<see cref="Activate"/> / <see cref="Release"/>, WR-02) — never left to the
/// MTA finalizer thread, and never through <c>Marshal.ReleaseComObject</c>, which rejects source-generated RCWs.
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
public sealed unsafe partial class DesktopWallpaperApplier : IWallpaperApplier
{
    private const string MethodName = "com";

    // CLSID_DesktopWallpaper {C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD}; the CsWin32 DesktopWallpaper class is deliberately
    // not generated (NativeMethods.txt, IN-13), so the CLSID lives here.
    private static readonly Guid ClsidDesktopWallpaper = new(0xC2CF3110, 0x460E, 0x4FC1, 0xB9, 0xD0, 0x8A, 0x1C, 0x0C, 0x9C, 0xC4, 0xBD);

    public ApplyResult Apply(string absolutePath)
    {
        if (!IsUsablePath(absolutePath))
        {
            return new ApplyResult(false, MethodName, null, null, "path not found");
        }

        IDesktopWallpaper wallpaper;
        try
        {
            wallpaper = Activate();
        }
        catch (Exception ex) when (ComFailure.IsActivationFailure(ex))
        {
            // Activation failure only (no IDesktopWallpaper on this session): degrade to the legacy SPI path.
            // Activate goes through Marshal.ThrowExceptionForHR, which maps HRESULTs to several types
            // (COMException, InvalidCastException, NotImplementedException, UnauthorizedAccessException, ...).
            Log.Warn($"apply failed method={MethodName} error=activation failed {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return SpiWallpaperFallback.Apply(absolutePath);
        }

        try
        {
            try
            {
                wallpaper.SetPosition(DESKTOP_WALLPAPER_POSITION.DWPOS_FILL);
                fixed (char* p = absolutePath)
                {
                    wallpaper.SetWallpaper(default, new PCWSTR(p));
                }
            }
            catch (Exception ex) when (ComFailure.IsCallFailure(ex))
            {
                // SetPosition / SetWallpaper failed after a good activation: E_INVALIDARG surfaces as
                // ArgumentException and E_ACCESSDENIED as UnauthorizedAccessException, not only as COMException.
                return new ApplyResult(false, MethodName, null, null, $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            // The desktop has changed: from here on the apply is Ok whatever the read-back does. A read-back throw
            // is logged and reported as an unknown read-back (null), never as a failed apply — otherwise the pipeline
            // would record the previous image while the shell already shows the new one (IN-14).
            string? readBack = null;
            string? position = null;
            try
            {
                readBack = ReadBack(wallpaper, default);
                DESKTOP_WALLPAPER_POSITION pos;
                wallpaper.GetPosition(&pos);
                position = pos.ToString();

                if (readBack is null || !string.Equals(readBack, absolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"apply readback-mismatch expected={absolutePath} actual={readBack ?? "-"}");
                }
            }
            catch (Exception ex) when (ComFailure.IsCallFailure(ex))
            {
                Log.Warn($"apply readback-failed expected={absolutePath} error={ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            return new ApplyResult(true, MethodName, readBack, position, null);
        }
        finally
        {
            Release(wallpaper);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MonitorHandle> GetAttachedMonitors()
    {
        IDesktopWallpaper wallpaper;
        try
        {
            wallpaper = Activate();
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
        finally
        {
            Release(wallpaper);
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
            wallpaper = Activate();
        }
        catch (Exception ex) when (ComFailure.IsActivationFailure(ex))
        {
            Log.Warn($"apply failed method={MethodName} error=activation failed {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return SpiWallpaperFallback.Apply(assignments[0].AbsolutePath);
        }

        try
        {
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
            }
            catch (Exception ex) when (ComFailure.IsCallFailure(ex))
            {
                return new ApplyResult(false, MethodName, null, null, $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            // Every monitor is set: the apply is Ok whatever the read-back does (IN-14, see Apply). Read back PER
            // monitor — never GetWallpaper(NULL) here: it answers "" as soon as monitors differ.
            string? primaryReadBack = null;
            string? position = null;
            try
            {
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

                DESKTOP_WALLPAPER_POSITION pos;
                wallpaper.GetPosition(&pos);
                position = pos.ToString();
            }
            catch (Exception ex) when (ComFailure.IsCallFailure(ex))
            {
                Log.Warn($"apply readback-failed monitors={assignments.Count} error={ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            return new ApplyResult(true, MethodName, primaryReadBack, position, null);
        }
        finally
        {
            Release(wallpaper);
        }
    }

    private static bool IsUsablePath(string? absolutePath) =>
        !string.IsNullOrEmpty(absolutePath) && Path.IsPathRooted(absolutePath) && File.Exists(absolutePath);

    /// <summary>
    /// One <c>IDesktopWallpaper</c> proxy for one operation (WR-02). The CsWin32 <c>DesktopWallpaper.CreateInstance</c>
    /// helper (no longer generated — it and <c>PInvoke.CoCreateInstance</c> are left out of <c>NativeMethods.txt</c>
    /// so this stays the only activation path, IN-13) marshalled through <see cref="ComInterfaceMarshaller{T}"/>,
    /// i.e. the shared ComWrappers cache, whose RCW drops its COM reference only from the finalizer thread — so
    /// proxies piled up between ticks under the non-concurrent GC. Activating through the raw
    /// <c>CoCreateInstance</c> and <see cref="UniqueComInterfaceMarshaller{T}"/> yields a unique-instance
    /// <see cref="ComObject"/>, the only kind <see cref="ComObject.FinalRelease"/> acts on, so <see cref="Release"/>
    /// can drop the reference deterministically on the calling STA thread. Throws the same types the CsWin32 helper
    /// threw (<c>Marshal.ThrowExceptionForHR</c> mapping; <see cref="InvalidCastException"/> when the interface is
    /// not implemented), so the callers' <see cref="ComFailure.IsActivationFailure"/> filters are unchanged.
    /// </summary>
    private static IDesktopWallpaper Activate()
    {
        Guid clsid = ClsidDesktopWallpaper;
        Guid iid = typeof(IDesktopWallpaper).GUID;
        void* raw = null;
        CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_SERVER, &iid, &raw).ThrowOnFailure();
        if (raw is null)
        {
            throw new InvalidCastException("CoCreateInstance(CLSID_DesktopWallpaper) succeeded without an IDesktopWallpaper pointer");
        }

        try
        {
            // The wrapper takes its own reference; the one CoCreateInstance handed us is released below.
            return UniqueComInterfaceMarshaller<IDesktopWallpaper>.ConvertToManaged(raw)
                ?? throw new InvalidCastException("CoCreateInstance(CLSID_DesktopWallpaper) produced no IDesktopWallpaper wrapper");
        }
        finally
        {
            Marshal.Release((nint)raw);
        }
    }

    /// <summary>Releases the proxy <see cref="Activate"/> produced; it must not be used afterwards. Same STA thread as the create.</summary>
    private static void Release(IDesktopWallpaper wallpaper)
    {
        // The runtime type is the (sealed) ComObject reached through IDynamicInterfaceCastable, hence the object hop.
        if ((object)wallpaper is ComObject proxy)
        {
            proxy.FinalRelease();
        }
    }

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial HRESULT CoCreateInstance(Guid* rclsid, void* pUnkOuter, CLSCTX dwClsContext, Guid* riid, void** ppv);

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
