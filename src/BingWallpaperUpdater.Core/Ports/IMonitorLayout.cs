namespace BingWallpaperUpdater.Core.Ports;

/// <summary>
/// Port implemented by the App adapter (<c>ScreenMonitorLayout</c> over <c>Screen.AllScreens</c>): the attached
/// monitors' sizes in physical pixels, read at the top of every tick so <c>ResolutionPolicy</c> can turn the "Auto"
/// resolution into a concrete one (SRC-07). Empty when unknown (no monitors enumerated); the caller treats that as UHD.
/// Core never touches WinForms or Win32.
/// </summary>
public interface IMonitorLayout
{
    /// <summary>Width and height of every attached monitor in physical pixels; empty when nothing could be enumerated.</summary>
    IReadOnlyList<(int Width, int Height)> Sizes();
}
