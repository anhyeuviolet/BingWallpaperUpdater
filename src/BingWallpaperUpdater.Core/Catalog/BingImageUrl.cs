using System.Globalization;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Catalog;

/// <summary>
/// Pure URL builder: every request URL is a function of (host, ID, resolution) and never copied from
/// remote text. UHD is the original <c>_UHD.jpg</c> with no <c>w</c>/<c>h</c> (the CDN re-encodes when
/// those are present — RESEARCH Pitfall 6); lower resolutions add the CDN resize parameters.
/// </summary>
public static class BingImageUrl
{
    public const string PrimaryHost = "www.bing.com";
    public const string RetryHost = "cn.bing.com";
    public const string CatalogHost = "raw.githubusercontent.com";

    /// <summary>
    /// <c>https://{host}/th?id={id}_UHD.jpg</c> when both dimensions are null; with both set,
    /// <c>...&amp;w={w}&amp;h={h}&amp;rs=1&amp;c=4</c>. Mixed null/non-null is a programming error.
    /// </summary>
    public static Uri Image(string host, ImageId id, int? width, int? height)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        if (width.HasValue != height.HasValue)
        {
            throw new ArgumentException("width and height must both be set or both be null");
        }

        string url = width is int w && height is int h
            ? string.Create(CultureInfo.InvariantCulture, $"https://{host}/th?id={id.Value}_UHD.jpg&w={w}&h={h}&rs=1&c=4")
            : $"https://{host}/th?id={id.Value}_UHD.jpg";
        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>
    /// HPImageArchive JSON endpoint. Bing caps <c>idx</c> at 7 and <c>n</c> at 8; <c>n=0</c> returns literal null.
    /// <paramref name="market"/> is percent-encoded so a user-edited value can only ever be the <c>mkt</c> value,
    /// never an extra query parameter or an invalid URI (WR-06; <c>Settings.Sanitize</c> normalises it first).
    /// </summary>
    public static Uri Archive(string host, int idx, int n, string market)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(market);
        ArgumentOutOfRangeException.ThrowIfNegative(idx);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(idx, 7);
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, 8);

        return new Uri(
            string.Create(CultureInfo.InvariantCulture, $"https://{host}/HPImageArchive.aspx?format=js&idx={idx}&n={n}&mkt={Uri.EscapeDataString(market)}"),
            UriKind.Absolute);
    }

    public static Uri CatalogReadme() =>
        new($"https://{CatalogHost}/niumoo/bing-wallpaper/main/README.md", UriKind.Absolute);

    public static Uri CatalogMonth(int year, int month)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(month, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(month, 12);
        return new Uri(
            string.Create(CultureInfo.InvariantCulture, $"https://{CatalogHost}/niumoo/bing-wallpaper/main/picture/{year:D4}-{month:D2}/README.md"),
            UriKind.Absolute);
    }

    /// <summary>Minimum accepted dimensions per resolution; a download smaller than this is rejected.</summary>
    public static (int Width, int Height) MinDimensions(string resolution) => resolution switch
    {
        "UHD" => (3840, 2160),
        "1920x1200" => (1920, 1200),
        "1920x1080" => (1920, 1080),
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "unknown resolution"),
    };
}
