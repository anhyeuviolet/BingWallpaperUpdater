using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Cache;

/// <summary>Outcome of a reconcile pass: the cleaned index, the IDs dropped, and the <c>*.part</c> names to delete.</summary>
public sealed record ReconcileResult(CacheIndex Index, IReadOnlyList<string> DroppedIds, IReadOnlyList<string> PartFiles);

/// <summary>
/// Pure startup reconcile (CACHE-05) over a directory listing: drop entries whose file is missing, name the
/// stray <c>*.part</c> files, ignore everything else. No I/O — <see cref="ImageCache"/> applies the result.
/// </summary>
public static class CacheReconciler
{
    public static ReconcileResult Reconcile(CacheIndex? loaded, IEnumerable<string> existingFileNames) =>
        throw new NotImplementedException();
}
