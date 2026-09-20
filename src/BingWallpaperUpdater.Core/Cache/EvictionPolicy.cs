using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Cache;

/// <summary>
/// Pure eviction rule (CACHE-01, CACHE-02): oldest by download time among the images that are neither
/// applied nor just added. No I/O, no logging — <see cref="ImageCache"/> deletes the files.
/// </summary>
public static class EvictionPolicy
{
    /// <summary>
    /// While more than <paramref name="maxImages"/> images would remain, picks the candidate with the smallest
    /// <see cref="CachedImage.DownloadedUtc"/> that is not in <paramref name="protectedIds"/> and not already
    /// chosen; ties resolve to the lowest index position. Stops early when only protected images remain, so a
    /// protected image is never returned even if the cap is still exceeded. Returns victims in eviction order.
    /// </summary>
    public static IReadOnlyList<CachedImage> SelectVictims(IReadOnlyList<CachedImage> images, IReadOnlySet<string> protectedIds, int maxImages)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(protectedIds);
        ArgumentOutOfRangeException.ThrowIfNegative(maxImages);

        var victims = new List<CachedImage>();
        var chosen = new HashSet<int>();

        while (images.Count - victims.Count > maxImages)
        {
            int best = -1;
            for (int i = 0; i < images.Count; i++)
            {
                if (chosen.Contains(i) || protectedIds.Contains(images[i].Id))
                {
                    continue;
                }

                if (best < 0 || images[i].DownloadedUtc < images[best].DownloadedUtc)
                {
                    best = i; // strict '<' keeps the earliest index on ties
                }
            }

            if (best < 0)
            {
                break; // everything left is protected
            }

            chosen.Add(best);
            victims.Add(images[best]);
        }

        return victims;
    }
}
