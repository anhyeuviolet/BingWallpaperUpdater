namespace BingWallpaperUpdater.Core.Model;

/// <summary>
/// Runtime state persisted at <c>%LocalAppData%\BingWallpaperUpdater\state.json</c>.
/// Phase 2 adds <c>NextDueUtc</c> and the scheduler fields.
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
}
