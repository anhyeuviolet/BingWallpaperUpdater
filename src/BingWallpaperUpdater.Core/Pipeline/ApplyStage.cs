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
}
