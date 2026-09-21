using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Ports;

namespace BingWallpaperUpdater.App;

/// <summary>
/// <see cref="IMonitorLayout"/> over <see cref="Screen.AllScreens"/> (RESEARCH Pattern 9): under PerMonitorV2 the
/// process sees each display's raw pixels, so <see cref="Screen.Bounds"/> is the physical size. Thread-agnostic —
/// WinForms refreshes its screen cache on display-settings changes, so the tick can call this off the UI thread.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class ScreenMonitorLayout : IMonitorLayout
{
    public IReadOnlyList<(int Width, int Height)> Sizes() =>
        Screen.AllScreens.Select(s => (s.Bounds.Width, s.Bounds.Height)).ToList();
}
