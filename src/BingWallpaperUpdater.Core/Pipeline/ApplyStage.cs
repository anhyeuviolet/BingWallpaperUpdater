using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Ports;

namespace BingWallpaperUpdater.Core.Pipeline;

/// <summary>
/// The apply step, ordered so the on-disk record can never lag the desktop (WR-07):
/// <list type="number">
/// <item><see cref="ImageCache.MarkApplied"/> first — the eviction protection is persisted in <c>index.json</c>
/// before anything touches the desktop; if that write fails nothing has changed and the exception propagates.</item>
/// <item><see cref="IWallpaperApplier.Apply"/>; on a failed result (or an escaping exception) the previous
/// <c>Applied</c> set is restored so the record still names the image that is actually on the desktop.</item>
/// <item><see cref="AppState"/> is updated and saved; a failure here is logged and does not undo anything,
/// because the desktop and the index are already consistent and <c>state.json</c> is not consulted by eviction.</item>
/// <item><c>apply ok</c> is logged last, so the line always describes a persisted state.</item>
/// </list>
/// Pure orchestration over the ports — the caller (the tray shell) provides the STA thread and the adapter.
/// Two entry points share that order (Plan 03-04 add-alongside, accepted debt): <see cref="Run"/> sets one image on
/// every monitor (NULL monitor ID — the same-image mode and the SPI fallback's only capability);
/// <see cref="RunPerMonitor"/> sets one image per attached monitor and records N applied IDs (WALL-03). Any change to
/// the protect -> apply -> rollback -> record sequence must be made in both; <c>ApplyStageTests</c> covers both.
/// </summary>
public static class ApplyStage
{
    /// <param name="appliedUtc">
    /// The time recorded as <see cref="AppState.LastAppliedUtc"/>; the scheduler passes its <see cref="TimeProvider"/>
    /// reading so fake-clock tests are deterministic. Null (the Phase 1 callers) falls back to the wall clock.
    /// </param>
    public static ApplyResult Run(IWallpaperApplier applier, string absolutePath, ImageId id, ImageCache cache, AppState state, string statePath, DateTimeOffset? appliedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(statePath);

        List<string> previous = [.. cache.Index.Applied];

        // 1. Protect first. A failed save leaves the desktop untouched; restore the in-memory set and let it propagate.
        try
        {
            cache.MarkApplied(id);
        }
        catch
        {
            cache.Index.Applied = previous;
            throw;
        }

        // 2. Change the desktop; any failure rolls the record back to what is really shown.
        ApplyResult apply;
        try
        {
            apply = applier.Apply(absolutePath);
        }
        catch
        {
            cache.RestoreApplied(previous);
            throw;
        }

        if (!apply.Ok)
        {
            cache.RestoreApplied(previous);
            Log.Warn($"apply failed method={apply.Method} error={apply.Error}");
            return apply;
        }

        // 3. Secondary record; never undoes a change that already happened.
        state.CurrentImageId = id.Value;
        state.LastAppliedUtc = appliedUtc ?? DateTimeOffset.UtcNow;
        try
        {
            state.Save(statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"state save failed path={statePath}", ex);
        }

        // 4. The log line describes persisted state.
        Log.Info($"apply ok method={apply.Method} id={id} path={absolutePath} readback={apply.ReadBackPath ?? "-"} position={apply.Position ?? "-"}");
        return apply;
    }

    /// <summary>
    /// The per-monitor apply (WALL-03), in the same four steps as <see cref="Run"/>: every ID in <paramref name="plan"/>
    /// is recorded in <c>index.json</c> (distinct, primary first) BEFORE the desktop changes; monitor <c>i</c> gets
    /// <c>plan[i % plan.Count]</c>; with no enumerable monitor (COM activation failed, SPI fallback) the primary
    /// <c>plan[0]</c> is set on all monitors through <see cref="IWallpaperApplier.Apply"/> and one ID is recorded;
    /// <see cref="AppState.CurrentImageId"/> is the primary; the log line keeps <c>id=</c> then <c>path=</c> (the
    /// smoke parser) and adds <c>monitors=&lt;n&gt; ids=&lt;a,b&gt;</c>.
    /// </summary>
    /// <param name="monitors">The attached monitors enumerated by the caller on the STA thread (never persisted).</param>
    /// <param name="plan">Ordered assignments, the primary at index 0; at least one entry, every path non-empty.</param>
    public static ApplyResult RunPerMonitor(IWallpaperApplier applier, IReadOnlyList<MonitorHandle> monitors, IReadOnlyList<(ImageId Id, string AbsolutePath)> plan, ImageCache cache, AppState state, string statePath, DateTimeOffset? appliedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(statePath);
        if (plan.Count == 0)
        {
            throw new ArgumentException("the per-monitor plan needs at least the primary image", nameof(plan));
        }

        foreach ((ImageId _, string absolutePath) in plan)
        {
            ArgumentException.ThrowIfNullOrEmpty(absolutePath, nameof(plan));
        }

        List<string> previous = [.. cache.Index.Applied];
        (ImageId primaryId, string primaryPath) = plan[0];

        // 1. Protect first — every ID that is about to be shown somewhere, or only the primary when the apply is
        // going to degrade to the single NULL-monitor call (then only the primary is on any desktop).
        IEnumerable<ImageId> toProtect = monitors.Count == 0 ? [primaryId] : plan.Select(p => p.Id);
        try
        {
            cache.MarkApplied(toProtect);
        }
        catch
        {
            cache.Index.Applied = previous;
            throw;
        }

        // 2. Change the desktop; any failure rolls the record back to what is really shown.
        ApplyResult apply;
        try
        {
            if (monitors.Count == 0)
            {
                apply = applier.Apply(primaryPath);
            }
            else
            {
                var assignments = new List<(MonitorHandle Monitor, string AbsolutePath)>(monitors.Count);
                for (int i = 0; i < monitors.Count; i++)
                {
                    assignments.Add((monitors[i], plan[i % plan.Count].AbsolutePath));
                }

                apply = applier.ApplyPerMonitor(assignments);
            }
        }
        catch
        {
            cache.RestoreApplied(previous);
            throw;
        }

        if (!apply.Ok)
        {
            cache.RestoreApplied(previous);
            Log.Warn($"apply failed method={apply.Method} error={apply.Error}");
            return apply;
        }

        // 3. Secondary record; never undoes a change that already happened.
        state.CurrentImageId = primaryId.Value;
        state.LastAppliedUtc = appliedUtc ?? DateTimeOffset.UtcNow;
        try
        {
            state.Save(statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"state save failed path={statePath}", ex);
        }

        // 4. The log line describes persisted state; `id=` then `path=` so the smoke's Get-AppliedId keeps parsing.
        Log.Info($"apply ok method={apply.Method} id={primaryId} path={primaryPath} monitors={monitors.Count} ids={string.Join(",", cache.Index.Applied)} readback={apply.ReadBackPath ?? "-"} position={apply.Position ?? "-"}");
        return apply;
    }
}
