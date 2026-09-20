using System.Globalization;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Cache;

/// <summary>
/// The per-user image cache: <c>cache\index.json</c> plus <c>YYYY-MM-DD_&lt;Name&gt;.jpg</c> files.
/// The index is the truth — only images listed here are ever applied. Eviction and reconcile arrive in Plan 03.
/// </summary>
public sealed class ImageCache
{
    private readonly string _cacheDir;
    private readonly string _indexPath;

    public ImageCache(string cacheDir, string indexPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        ArgumentException.ThrowIfNullOrEmpty(indexPath);
        _cacheDir = cacheDir;
        _indexPath = indexPath;
    }

    /// <summary>Cap on validated images in the cache (CACHE-01); <c>.part</c> files never count.</summary>
    public const int MaxImages = 10;

    public CacheIndex Index { get; private set; } = new();

    public string CacheDir => _cacheDir;

    /// <summary>
    /// Startup reconcile (CACHE-05): load the index, drop entries whose file is missing, delete stray
    /// <c>*.part</c> files, leave every other file alone, and save only when something changed.
    /// </summary>
    public void Reconcile() => throw new NotImplementedException();

    /// <summary>
    /// Appends (replacing any entry with the same id + resolution), evicts down to <see cref="MaxImages"/>
    /// while protecting the applied and the just-added image, deletes victim files best-effort, saves once.
    /// </summary>
    public CachedImage Add(CachedImage image) => throw new NotImplementedException();

    /// <summary>Loads <c>index.json</c>; a missing or corrupt index becomes an empty one (never throws).</summary>
    public void Load()
    {
        Index = AtomicJsonFile.Load(_indexPath, CoreJsonContext.Default.CacheIndex) ?? new CacheIndex();
        Index.Applied ??= [];
        Index.Images ??= [];
    }

    public CachedImage? TryGet(ImageId id, string resolution) =>
        Index.Images.FirstOrDefault(i =>
            string.Equals(i.Id, id.Value, StringComparison.Ordinal)
            && string.Equals(i.Resolution, resolution, StringComparison.Ordinal));

    /// <summary>
    /// <c>{date}_{Name}.jpg</c> for UHD, <c>{date}_{Name}.{w}x{h}.jpg</c> otherwise. The date is the catalog
    /// date, else Bing's start date reformatted, else today (UTC). Both segments are regex-constrained
    /// (<c>\d{4}-\d{2}-\d{2}</c>, <c>[A-Za-z0-9]+</c>) so remote text cannot inject path characters (T-01-02).
    /// </summary>
    public string FileNameFor(CatalogEntry entry, string resolution, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string date = ResolveDate(entry, nowUtc);
        string name = entry.Id.Name;
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("entry has no parsable image name", nameof(entry));
        }

        if (string.Equals(resolution, "UHD", StringComparison.Ordinal))
        {
            return $"{date}_{name}.jpg";
        }

        (int w, int h) = BingImageUrl.MinDimensions(resolution);
        return string.Create(CultureInfo.InvariantCulture, $"{date}_{name}.{w}x{h}.jpg");
    }

    /// <summary>
    /// Returns the cached entry, downloading it first when absent: primary host, then one retry on the
    /// mirror. Returns null when both attempts are rejected; nothing is written to the index in that case.
    /// </summary>
    public async Task<CachedImage?> EnsureAsync(CatalogEntry entry, string resolution, HttpGateway http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(http);

        if (TryGet(entry.Id, resolution) is { } hit && File.Exists(Path.Combine(_cacheDir, hit.File)))
        {
            Log.Info($"cache hit id={hit.Id} file={hit.File}");
            return hit;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string fileName = FileNameFor(entry, resolution, now);
        string finalPath = Path.Combine(_cacheDir, fileName);
        (int minW, int minH) = BingImageUrl.MinDimensions(resolution);
        bool isUhd = string.Equals(resolution, "UHD", StringComparison.Ordinal);

        foreach (string host in new[] { BingImageUrl.PrimaryHost, BingImageUrl.RetryHost })
        {
            Uri url = isUhd
                ? BingImageUrl.Image(host, entry.Id, null, null)
                : BingImageUrl.Image(host, entry.Id, minW, minH);

            DownloadResult result = await http.DownloadJpegAsync(url, finalPath, minW, minH, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                Log.Info($"download rejected host={host} reason={result.Reason}");
                continue;
            }

            var cached = new CachedImage
            {
                Id = entry.Id.Value,
                Market = entry.Id.Market,
                Date = entry.Date,
                StartDate = entry.StartDate,
                Title = entry.Title,
                Copyright = entry.Copyright,
                CopyrightLink = entry.CopyrightLink,
                Resolution = resolution,
                Width = result.Width,
                Height = result.Height,
                File = fileName,
                Bytes = result.Bytes,
                SourceUrl = url.ToString(),
                DownloadedUtc = DateTimeOffset.UtcNow,
            };

            Index.Images.RemoveAll(i =>
                string.Equals(i.Id, cached.Id, StringComparison.Ordinal)
                && string.Equals(i.Resolution, resolution, StringComparison.Ordinal));
            Index.Images.Add(cached);
            Save();
            Log.Info($"cache add id={cached.Id} file={cached.File} bytes={cached.Bytes} dims={cached.Width}x{cached.Height}");
            return cached;
        }

        return null;
    }

    /// <summary>Records the image currently set on the desktop (single entry in Phase 1) and saves.</summary>
    public void MarkApplied(ImageId id)
    {
        Index.Applied = [id.Value];
        Save();
    }

    public void Save() => AtomicJsonFile.Save(_indexPath, Index, CoreJsonContext.Default.CacheIndex);

    private static string ResolveDate(CatalogEntry entry, DateTimeOffset nowUtc)
    {
        if (!string.IsNullOrEmpty(entry.Date)
            && DateTime.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(entry.StartDate)
            && DateTime.TryParseExact(entry.StartDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime s))
        {
            return s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
