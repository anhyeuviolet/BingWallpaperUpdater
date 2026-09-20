using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;

namespace BingWallpaperUpdater.Core.Model;

/// <summary>
/// Runtime state persisted at <c>%LocalAppData%\BingWallpaperUpdater\state.json</c>.
/// The Phase 2 scheduler fields are additive and nullable: a Phase 1 file loads with them null and
/// <see cref="SchemaVersion"/> stays 1 (D-08).
/// </summary>
public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>ETag of the last README body stored at <c>cache\catalog.md</c> (for <c>If-None-Match</c>).</summary>
    public string? CatalogEtag { get; set; }
    public DateTimeOffset? CatalogFetchedUtc { get; set; }

    /// <summary>ID of the image last applied successfully.</summary>
    public string? CurrentImageId { get; set; }
    public DateTimeOffset? LastAppliedUtc { get; set; }

    /// <summary>
    /// Absolute UTC time of the next scheduled rotation (D-06). The heartbeat compares the clock against this value;
    /// re-arm is always <c>now + interval</c>, never <c>nextDue + n * interval</c>, so missed intervals collapse into
    /// one catch-up tick. Null until the first tick has run.
    /// </summary>
    public DateTimeOffset? NextDueUtc { get; set; }

    /// <summary>When the catalog was last asked, success or failure (RESEARCH A4; Phase 3 shows it as "Last checked").</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>
    /// Newest catalog ID the app has successfully applied (D-03). "New image" means the catalog newest differs from
    /// this, not from <see cref="CurrentImageId"/>, so a user's Next step-back is never undone by an unchanged
    /// catalog. Written only after a successful apply of that image, or seeded by the Phase 1 upgrade guard.
    /// </summary>
    public string? LastSeenNewestId { get; set; }

    /// <summary>Loads the file, or returns a fresh state when it is missing or corrupt (no write-back needed).</summary>
    public static AppState LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return AtomicJsonFile.Load(path, CoreJsonContext.Default.AppState) ?? new AppState();
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        AtomicJsonFile.Save(path, this, CoreJsonContext.Default.AppState);
    }
}
