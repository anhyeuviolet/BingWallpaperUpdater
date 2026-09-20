using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Scheduling;

namespace BingWallpaperUpdater.Core.Rotation;

/// <summary>
/// The pure decision table shared by scheduled ticks and the tray "Next wallpaper" item (RESEARCH Pattern 3;
/// D-03, D-04, D-05, D-11, D-13, D-14). No I/O and no logging, except the injected <c>fileExists</c> predicate in
/// <see cref="Candidates"/>; <c>RotationService</c> performs the download and the apply for the row returned here.
/// IDs are compared only with <see cref="StringComparison.Ordinal"/> (T-02-03).
/// </summary>
public static class RotationDecider
{
    /// <summary>
    /// The images rotation may pick from: one entry per ID, preferring <paramref name="preferredResolution"/> and
    /// then the largest width (RESEARCH A8, Pitfall 8), only entries whose file exists, ordered newest first.
    /// </summary>
    public static IReadOnlyList<CachedImage> Candidates(IEnumerable<CachedImage> images, string preferredResolution, Func<CachedImage, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentException.ThrowIfNullOrEmpty(preferredResolution);
        ArgumentNullException.ThrowIfNull(fileExists);

        var best = new Dictionary<string, CachedImage>(StringComparer.Ordinal);
        foreach (CachedImage image in images)
        {
            if (string.IsNullOrEmpty(image.Id) || !fileExists(image))
            {
                continue;
            }

            if (!best.TryGetValue(image.Id, out CachedImage? current) || Prefer(image, current, preferredResolution))
            {
                best[image.Id] = image;
            }
        }

        return OrderNewestFirst(best.Values);
    }

    /// <summary><c>Date</c> descending (ISO dates sort ordinally; null last), then <c>DownloadedUtc</c> descending.</summary>
    public static IReadOnlyList<CachedImage> OrderNewestFirst(IEnumerable<CachedImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        return images
            .OrderByDescending(i => i.Date ?? string.Empty, StringComparer.Ordinal)
            .ThenByDescending(i => i.DownloadedUtc)
            .ToList();
    }

    /// <summary>
    /// The entry after <paramref name="currentId"/> in <paramref name="newestFirst"/>, wrapping from the oldest back
    /// to the newest (D-05). An unknown or null current maps to the newest (index 0, RESEARCH A8); an empty list
    /// yields null.
    /// </summary>
    public static CachedImage? StepOlder(IReadOnlyList<CachedImage> newestFirst, string? currentId)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        if (newestFirst.Count == 0)
        {
            return null;
        }

        int idx = IndexOf(newestFirst, currentId);
        return newestFirst[idx < 0 ? 0 : (idx + 1) % newestFirst.Count];
    }

    /// <summary>A uniformly random entry whose ID is not <paramref name="currentId"/>, or null when none exists (D-04, ROT-03).</summary>
    public static CachedImage? RandomOther(IReadOnlyList<CachedImage> images, string? currentId, Random random)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(random);

        List<CachedImage> others = images.Where(i => !string.Equals(i.Id, currentId, StringComparison.Ordinal)).ToList();
        return others.Count == 0 ? null : others[random.Next(others.Count)];
    }

    /// <summary>
    /// The decision table, rows evaluated top to bottom (the exact order is a contract; see the tests):
    /// <list type="number">
    /// <item>R1 upgrade guard: no <c>LastSeenNewestId</c> yet and the current image is the catalog newest with its file present -> <c>SeedLastSeen</c>.</item>
    /// <item>R2 catalog newest differs from <c>LastSeenNewestId</c> -> <c>ApplyNew</c> (D-03; both modes, D-04).</item>
    /// <item>R3 current image missing (null ID or file gone) -> newest from the catalog, else newest cached, else <c>NoOp(empty-cache)</c> (D-11).</item>
    /// <item>R4 <see cref="TickReason.Retry"/> never rotates -> <c>NoOp(retry)</c> (D-13).</item>
    /// <item>R5 <see cref="TickReason.Next"/> with two or more candidates -> step older / random other; otherwise <c>NoOp(single-image)</c> (D-05, D-15).</item>
    /// <item>R6 a due <see cref="TickReason.Interval"/> or <see cref="TickReason.Startup"/> tick in random mode with two or more candidates -> random other (D-04, D-14).</item>
    /// <item>R7 <see cref="TickReason.Startup"/> not due -> <c>NoOp(not-due)</c> (D-11).</item>
    /// <item>R8 otherwise <c>NoOp(unchanged)</c> (ROT-02; random with one image behaves as newest, ROT-03).</item>
    /// </list>
    /// </summary>
    /// <param name="reason">Why the tick runs.</param>
    /// <param name="randomMode">True for <c>random</c> mode.</param>
    /// <param name="newest">The catalog's newest entry, or null when the fetch failed.</param>
    /// <param name="state">Persisted state; only <c>CurrentImageId</c> and <c>LastSeenNewestId</c> are read.</param>
    /// <param name="candidates">Output of <see cref="Candidates"/> (newest first, files present).</param>
    /// <param name="intervalDue">Whether the persisted schedule was due when the tick started (captured before any re-arm).</param>
    /// <param name="random">Random source for random mode.</param>
    public static RotationDecision Decide(TickReason reason, bool randomMode, CatalogEntry? newest, AppState state, IReadOnlyList<CachedImage> candidates, bool intervalDue, Random random)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(random);

        string? current = state.CurrentImageId;
        bool currentPresent = current is not null && IndexOf(candidates, current) >= 0;

        // R1
        if (newest is not null
            && state.LastSeenNewestId is null
            && string.Equals(newest.Id.Value, current, StringComparison.Ordinal)
            && currentPresent)
        {
            return new RotationDecision(DecisionKind.SeedLastSeen, newest, null, "seed");
        }

        // R2
        if (newest is not null && !string.Equals(newest.Id.Value, state.LastSeenNewestId, StringComparison.Ordinal))
        {
            return new RotationDecision(DecisionKind.ApplyNew, newest, null, "new");
        }

        // R3
        if (!currentPresent)
        {
            if (newest is not null)
            {
                return new RotationDecision(DecisionKind.ApplyNew, newest, null, "missing-current");
            }

            return candidates.Count > 0
                ? new RotationDecision(DecisionKind.StepOlder, null, candidates[0], "missing-current")
                : new RotationDecision(DecisionKind.NoOp, null, null, "empty-cache");
        }

        // R4
        if (reason == TickReason.Retry)
        {
            return new RotationDecision(DecisionKind.NoOp, null, null, "retry");
        }

        // R5
        if (reason == TickReason.Next)
        {
            if (candidates.Count < 2)
            {
                return new RotationDecision(DecisionKind.NoOp, null, null, "single-image");
            }

            return randomMode
                ? Pick(DecisionKind.Random, RandomOther(candidates, current, random), "next")
                : Pick(DecisionKind.StepOlder, StepOlder(candidates, current), "next");
        }

        // R6
        if ((reason == TickReason.Interval || (reason == TickReason.Startup && intervalDue)) && randomMode && candidates.Count >= 2)
        {
            return Pick(DecisionKind.Random, RandomOther(candidates, current, random), "interval");
        }

        // R7
        if (reason == TickReason.Startup && !intervalDue)
        {
            return new RotationDecision(DecisionKind.NoOp, null, null, "not-due");
        }

        // R8
        return new RotationDecision(DecisionKind.NoOp, null, null, "unchanged");
    }

    private static RotationDecision Pick(DecisionKind kind, CachedImage? target, string why) =>
        target is null
            ? new RotationDecision(DecisionKind.NoOp, null, null, "single-image")
            : new RotationDecision(kind, null, target, why);

    private static int IndexOf(IReadOnlyList<CachedImage> images, string? id)
    {
        if (id is null)
        {
            return -1;
        }

        for (int i = 0; i < images.Count; i++)
        {
            if (string.Equals(images[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>True when <paramref name="candidate"/> should replace <paramref name="current"/> as the entry for its ID.</summary>
    private static bool Prefer(CachedImage candidate, CachedImage current, string preferredResolution)
    {
        bool candidatePreferred = string.Equals(candidate.Resolution, preferredResolution, StringComparison.Ordinal);
        bool currentPreferred = string.Equals(current.Resolution, preferredResolution, StringComparison.Ordinal);
        if (candidatePreferred != currentPreferred)
        {
            return candidatePreferred;
        }

        return candidate.Width > current.Width;
    }
}
