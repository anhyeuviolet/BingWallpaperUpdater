using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Pipeline;

/// <summary>
/// The single launch-time run: catalog -> cache -> absolute file path. Runs on the thread pool; the caller
/// hops to the UI thread to apply. Phase 2 wraps these stages in the rotation service.
/// </summary>
public static class FirstRunPipeline
{
    public static async Task<(string AbsolutePath, ImageId Id)?> EnsureTodayAsync(
        Settings settings,
        AppState state,
        CatalogService catalog,
        ImageCache cache,
        HttpGateway http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(http);

        // CACHE-05: reconcile the cache directory before any network call (drops missing files, deletes *.part).
        cache.Reconcile();

        CatalogEntry? entry = await catalog.GetNewestAsync(settings.Market, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return null; // already logged by the catalog stage
        }

        CachedImage? cached = await cache.EnsureAsync(entry, settings.Resolution, http, ct).ConfigureAwait(false);
        if (cached is null)
        {
            Log.Warn($"pipeline failed stage=download error=no host delivered id={entry.Id}");
            return null;
        }

        string absolutePath = Path.GetFullPath(Path.Combine(cache.CacheDir, cached.File));
        return (absolutePath, entry.Id);
    }
}
