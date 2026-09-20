using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Cache;

/// <summary>
/// Cache file naming (CACHE-03): <c>YYYY-MM-DD_&lt;Name&gt;.jpg</c> for UHD, <c>YYYY-MM-DD_&lt;Name&gt;.{w}x{h}.jpg</c>
/// otherwise, with a total date fallback chain (catalog date, Bing start date, UTC download date).
/// </summary>
public static class CacheFileName
{
    public static string For(CatalogEntry entry, string resolution, DateTimeOffset nowUtc) =>
        throw new NotImplementedException();

    public static string DatePrefix(string? catalogDate, string? startDate, DateTimeOffset nowUtc) =>
        throw new NotImplementedException();
}
