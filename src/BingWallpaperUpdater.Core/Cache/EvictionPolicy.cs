using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Cache;

/// <summary>
/// Pure eviction rule (CACHE-01, CACHE-02): oldest by download time among the images that are neither
/// applied nor just added. No I/O, no logging — <see cref="ImageCache"/> deletes the files.
/// </summary>
public static class EvictionPolicy
{
    public static IReadOnlyList<CachedImage> SelectVictims(IReadOnlyList<CachedImage> images, IReadOnlySet<string> protectedIds, int maxImages) =>
        throw new NotImplementedException();
}
