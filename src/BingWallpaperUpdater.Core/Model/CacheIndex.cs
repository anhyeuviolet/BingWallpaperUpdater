namespace BingWallpaperUpdater.Core.Model;

/// <summary>
/// On-disk shape of <c>cache\index.json</c>. The index is the source of truth for what is cached;
/// files not listed here are never applied.
/// </summary>
public sealed class CacheIndex
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Image IDs currently set as wallpaper (exactly one entry in Phase 1).</summary>
    public List<string> Applied { get; set; } = [];

    public List<CachedImage> Images { get; set; } = [];
}

public sealed class CachedImage
{
    public string Id { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty;
    public string? Date { get; set; }
    public string? StartDate { get; set; }
    public string? Title { get; set; }
    public string? Copyright { get; set; }
    public string? CopyrightLink { get; set; }

    /// <summary>"UHD", "1920x1200" or "1920x1080".</summary>
    public string Resolution { get; set; } = "UHD";
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>
    /// File name relative to the cache directory, e.g. <c>2026-09-20_AlphornBavaria_EN-US6200857270.jpg</c>
    /// (earlier builds wrote <c>2026-09-20_AlphornBavaria.jpg</c>; whatever is recorded here is what the entry owns).
    /// </summary>
    public string File { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset DownloadedUtc { get; set; }
}
