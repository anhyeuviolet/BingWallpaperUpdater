using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Rotation;

/// <summary>What a tick should do to the desktop, as chosen by <see cref="RotationDecider.Decide"/>.</summary>
public enum DecisionKind
{
    /// <summary>Download (if needed) and apply the catalog's newest image (<see cref="RotationDecision.Entry"/>).</summary>
    ApplyNew,

    /// <summary>Apply the next older cached image (<see cref="RotationDecision.Target"/>), wrapping to the newest (D-05).</summary>
    StepOlder,

    /// <summary>Apply a random cached image other than the current one (<see cref="RotationDecision.Target"/>) (D-04).</summary>
    Random,

    /// <summary>Phase 1 upgrade guard: record the newest ID as seen without touching the desktop (RESEARCH Pitfall 3).</summary>
    SeedLastSeen,

    /// <summary>Leave the desktop alone; <see cref="RotationDecision.Why"/> says why.</summary>
    NoOp,
}

/// <summary>
/// One row of the decision table (RESEARCH Pattern 3) as data. <paramref name="Why"/> is a stable token that appears
/// in the <c>tick done ... why=</c> log line and in tests: <c>new</c>, <c>seed</c>, <c>missing-current</c>,
/// <c>retry</c>, <c>next</c>, <c>interval</c>, <c>single-image</c>, <c>unchanged</c>, <c>empty-cache</c>, <c>not-due</c>.
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="Entry">The catalog entry to ensure and apply (<see cref="DecisionKind.ApplyNew"/>, <see cref="DecisionKind.SeedLastSeen"/>).</param>
/// <param name="Target">The cached image to apply (<see cref="DecisionKind.StepOlder"/>, <see cref="DecisionKind.Random"/>).</param>
/// <param name="Why">Stable reason token for logs and tests.</param>
public sealed record RotationDecision(DecisionKind Kind, CatalogEntry? Entry, CachedImage? Target, string Why);
