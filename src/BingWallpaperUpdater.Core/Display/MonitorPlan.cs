using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Display;

/// <summary>
/// Pure per-monitor assignment rule (WALL-03): turns the tick's single decision (one primary image) into an ordered
/// list of image IDs, one per attached monitor. No I/O, no logging, no clock — deterministic for equal inputs, so a
/// dock/undock re-apply reproduces the same set instead of reshuffling it. Index 0 is always the primary; the others
/// are the next older candidates after the primary in <c>RotationDecider.OrderNewestFirst</c> order, wrapping from
/// the oldest back to the newest. With fewer distinct candidates than monitors every monitor gets the primary
/// ("same image on all until enough are cached"), never a partial mix.
/// </summary>
public static class MonitorPlan
{
    /// <param name="newestFirst">The rotation candidates, newest first (one entry per ID is expected; duplicates are collapsed).</param>
    /// <param name="primaryId">The decision's image; always index 0 of the result, even when absent from <paramref name="newestFirst"/>.</param>
    /// <param name="monitorCount">Attached monitors; must be positive (the caller uses <c>Math.Max(1, n)</c>).</param>
    /// <returns>
    /// Exactly <paramref name="monitorCount"/> IDs. <c>[primary] * n</c> when <paramref name="monitorCount"/> is 1 or the
    /// distinct candidates are fewer than the monitors; otherwise the primary followed by the next older distinct
    /// candidates, wrapping. A primary that is not among the candidates is followed by the newest ones.
    /// </returns>
    public static IReadOnlyList<string> Build(IReadOnlyList<CachedImage> newestFirst, string primaryId, int monitorCount)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        ArgumentException.ThrowIfNullOrEmpty(primaryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(monitorCount);

        var distinct = new List<string>(newestFirst.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CachedImage image in newestFirst)
        {
            if (!string.IsNullOrEmpty(image.Id) && seen.Add(image.Id))
            {
                distinct.Add(image.Id);
            }
        }

        if (monitorCount == 1 || distinct.Count < monitorCount)
        {
            return Enumerable.Repeat(primaryId, monitorCount).ToList();
        }

        // Neighbours start right after the primary; -1 (absent) makes the first neighbour the newest candidate. Because
        // distinct.Count >= monitorCount, the wrap can never land on the primary's own index again.
        int start = distinct.FindIndex(id => string.Equals(id, primaryId, StringComparison.Ordinal));
        var ids = new List<string>(monitorCount) { primaryId };
        for (int k = 1; k < monitorCount; k++)
        {
            ids.Add(distinct[(start + k) % distinct.Count]);
        }

        return ids;
    }
}
