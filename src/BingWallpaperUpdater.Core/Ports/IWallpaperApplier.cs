namespace BingWallpaperUpdater.Core.Ports;

/// <summary>
/// Port implemented by the Windows adapter (<c>DesktopWallpaperApplier</c>). Core never touches Win32.
/// Two apply entry points live side by side (Plan 03-04 add-alongside): <see cref="Apply"/> is the same-image path
/// (NULL monitor = every monitor, and the only thing the SPI fallback can do); <see cref="ApplyPerMonitor"/> sets one
/// image per attached monitor (WALL-03). Both must be called on the STA (WinForms UI) thread.
/// </summary>
public interface IWallpaperApplier
{
    /// <summary>Sets <paramref name="absolutePath"/> on all monitors with Fill and reads the result back.</summary>
    ApplyResult Apply(string absolutePath);

    /// <summary>
    /// The monitors attached right NOW, enumerated on the STA thread through <c>IDesktopWallpaper</c> and filtered
    /// to those the shell reports a rectangle for (detached entries with a leftover image are skipped). Empty when
    /// <c>IDesktopWallpaper</c> cannot be activated on this session — the caller then degrades to <see cref="Apply"/>.
    /// The handles are valid for the current apply only; never persist them (device paths churn on dock/undock).
    /// </summary>
    IReadOnlyList<MonitorHandle> GetAttachedMonitors();

    /// <summary>
    /// <c>SetPosition(Fill)</c> once, then one <c>SetWallpaper(monitor, path)</c> per assignment, then a read-back per
    /// monitor (never the NULL-monitor read-back, which is empty when monitors differ). <see cref="ApplyResult.ReadBackPath"/>
    /// is the first assignment's read-back; a mismatch on any monitor is logged, not a failure. Falls back to the
    /// single SPI apply of the first path when activation fails.
    /// </summary>
    ApplyResult ApplyPerMonitor(IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> assignments);
}

/// <param name="Ok">True when the API accepted the path (a read-back mismatch is logged, not a failure).</param>
/// <param name="Method">"com" or "spi".</param>
/// <param name="ReadBackPath">What the shell reports after the apply, or null.</param>
/// <param name="Position">e.g. "DWPOS_FILL" for COM; null for SPI.</param>
public sealed record ApplyResult(bool Ok, string Method, string? ReadBackPath, string? Position, string? Error);

/// <summary>
/// One attached monitor as the shell enumerates it at apply time. Opaque to Core: the device path is only ever handed
/// back to <see cref="IWallpaperApplier.ApplyPerMonitor"/> within the same apply and is never written to
/// <c>settings.json</c>, <c>state.json</c> or <c>index.json</c> (WALL-03 locked decision, 01-RESEARCH Pitfall 8).
/// </summary>
/// <param name="DevicePath">The shell's monitor ID (a <c>\\?\DISPLAY#...</c> path).</param>
/// <param name="Width">Rectangle width in physical pixels.</param>
/// <param name="Height">Rectangle height in physical pixels.</param>
/// <param name="Left">Rectangle left edge in the virtual desktop.</param>
/// <param name="Top">Rectangle top edge in the virtual desktop.</param>
public sealed record MonitorHandle(string DevicePath, int Width, int Height, int Left, int Top);
