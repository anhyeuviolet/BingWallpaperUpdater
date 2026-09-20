namespace BingWallpaperUpdater.Core.Scheduling;

/// <summary>
/// Why a rotation tick is running. Every caller of <c>RotationService.RunTickAsync</c> — the first heartbeat after
/// launch, a due heartbeat, a backoff retry, the tray "Next wallpaper" item — passes one of these so the decision
/// table and the log line know the intent (D-01).
/// </summary>
public enum TickReason
{
    /// <summary>The first tick after launch (D-11): catch up if the persisted schedule is due, never re-apply otherwise.</summary>
    Startup,

    /// <summary>A heartbeat found <c>now &gt;= NextDueUtc</c> (D-06).</summary>
    Interval,

    /// <summary>A backoff retry after a failed fetch (D-13); only the fetch is retried, the cache is never rotated.</summary>
    Retry,

    /// <summary>The user chose "Next wallpaper" (D-05); always does something visible when the cache holds two or more images.</summary>
    Next,
}
