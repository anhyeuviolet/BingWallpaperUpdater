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
    private const string PartSuffix = ".part";

    /// <summary>
    /// <paramref name="loaded"/> null (missing or corrupt index) yields an empty index. <paramref name="existingFileNames"/>
    /// are bare file names (no paths) compared ordinal-ignore-case. An entry whose <see cref="CachedImage.File"/> is
    /// not a bare name (contains a separator or <c>..</c>) is dropped like a missing file so it can never point
    /// outside the cache directory (T-01-15). Applied IDs without a surviving entry are dropped too.
    /// </summary>
    public static ReconcileResult Reconcile(CacheIndex? loaded, IEnumerable<string> existingFileNames)
    {
        ArgumentNullException.ThrowIfNull(existingFileNames);

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (string name in existingFileNames)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(name);
            }
            else
            {
                present.Add(name);
            }
        }

        var index = new CacheIndex { SchemaVersion = loaded?.SchemaVersion ?? 1 };
        var dropped = new List<string>();
        foreach (CachedImage image in loaded?.Images ?? [])
        {
            if (image is null)
            {
                continue;
            }

            if (IsBareFileName(image.File) && present.Contains(image.File))
            {
                index.Images.Add(image);
            }
            else
            {
                dropped.Add(image.Id);
            }
        }

        var survivingIds = new HashSet<string>(index.Images.Select(i => i.Id), StringComparer.Ordinal);
        foreach (string applied in loaded?.Applied ?? [])
        {
            if (applied is not null && survivingIds.Contains(applied))
            {
                index.Applied.Add(applied);
            }
        }

        return new ReconcileResult(index, dropped, parts);
    }

    /// <summary>True when <paramref name="file"/> is a plain name inside the directory: no separators, no <c>..</c>.</summary>
    public static bool IsBareFileName(string? file) =>
        !string.IsNullOrEmpty(file)
        && !file.Contains("..", StringComparison.Ordinal)
        && file.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) < 0
        && string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal);
}
