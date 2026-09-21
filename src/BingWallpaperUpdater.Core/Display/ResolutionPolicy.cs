using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Display;

/// <summary>
/// Pure "Auto" resolution rule (SRC-07): the smallest known resolution that covers the largest attached monitor by
/// pixel area. No I/O, no logging, no clock — the monitor sizes come from the caller (<c>IMonitorLayout</c>), and the
/// result is always one of the three concrete values <c>BingImageUrl.MinDimensions</c> accepts, so "Auto" can never
/// reach the download stage or a cache entry.
/// </summary>
public static class ResolutionPolicy
{
    private const string Uhd = "UHD";
    private const string Wuxga = "1920x1200";
    private const string FullHd = "1920x1080";

    /// <summary>
    /// A concrete setting (anything other than <see cref="Settings.AutoResolution"/>) is returned unchanged. For "Auto":
    /// no monitors -> UHD; otherwise the largest monitor by area decides — either dimension above 1920x1200 -> UHD
    /// (2560x1440, 3440x1440, 3840x2160 ...), taller than 1080 -> 1920x1200, else 1920x1080 (1920x1080, 1366x768 ...).
    /// </summary>
    public static string Resolve(string setting, IReadOnlyList<(int Width, int Height)> monitors)
    {
        ArgumentException.ThrowIfNullOrEmpty(setting);
        ArgumentNullException.ThrowIfNull(monitors);

        if (!string.Equals(setting, Settings.AutoResolution, StringComparison.Ordinal))
        {
            return setting;
        }

        if (monitors.Count == 0)
        {
            return Uhd;
        }

        (int w, int h) = monitors.MaxBy(m => (long)m.Width * m.Height);
        if (w > 1920 || h > 1200)
        {
            return Uhd;
        }

        return h > 1080 ? Wuxga : FullHd;
    }
}
