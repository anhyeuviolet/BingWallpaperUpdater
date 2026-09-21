using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Cache;

/// <summary>
/// The per-user image cache: <c>cache\index.json</c> plus <c>YYYY-MM-DD_&lt;Name&gt;_&lt;MARKET&gt;&lt;digits&gt;.jpg</c>
/// files (see <see cref="CacheFileName"/>). The index is the truth — only images listed here are ever applied,
/// and an entry's <see cref="CachedImage.File"/> is honoured whatever naming rule produced it. This class owns
/// every delete the app performs: eviction removes only files named in the index and never a file another
/// surviving entry still names, reconcile removes only <c>*.part</c> files inside the cache directory, and
/// unknown files are never touched (T-01-15). The applied image is never evicted (CACHE-02; CLAUDE.md
/// "Deleting the wallpaper file after setting it").
/// </summary>
public sealed class ImageCache
{
    private readonly string _cacheDir;
    private readonly string _indexPath;
    private readonly TimeProvider _time;

    /// <param name="time">
    /// The clock that stamps <see cref="CachedImage.DownloadedUtc"/> and dates fallback file names; defaults to
    /// <see cref="TimeProvider.System"/>. Pass the same provider the scheduler uses so every timestamp the app
    /// records comes from one clock.
    /// </param>
    public ImageCache(string cacheDir, string indexPath, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        ArgumentException.ThrowIfNullOrEmpty(indexPath);
        _cacheDir = cacheDir;
        _indexPath = indexPath;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Cap on validated images in the cache (CACHE-01); <c>.part</c> files never count.</summary>
    public const int MaxImages = 10;

    public CacheIndex Index { get; private set; } = new();

    public string CacheDir => _cacheDir;

    /// <summary>Loads <c>index.json</c>; a missing or corrupt index becomes an empty one (never throws).</summary>
    public void Load()
    {
        Index = AtomicJsonFile.Load(_indexPath, CoreJsonContext.Default.CacheIndex) ?? new CacheIndex();
        Index.Applied ??= [];
        Index.Images ??= [];

        // NextSeq must stay above every Seq already handed out, whatever a hand-edited or older index says.
        long maxSeq = Index.Images.Count == 0 ? 0 : Index.Images.Max(i => i.Seq);
        Index.NextSeq = Math.Max(Math.Max(Index.NextSeq, 1), maxSeq + 1);
    }

    /// <summary>
    /// Startup reconcile (CACHE-05): load the index, drop entries whose file is missing, delete stray
    /// <c>*.part</c> files, leave every other file alone, and save only when something changed.
    /// Runs before any network call; a hand-edited folder or corrupt index never throws.
    /// </summary>
    public void Reconcile()
    {
        Load();

        IEnumerable<string> names = Directory.Exists(_cacheDir)
            ? Directory.EnumerateFiles(_cacheDir).Select(f => Path.GetFileName(f))
            : [];

        int appliedBefore = Index.Applied.Count;
        ReconcileResult result = CacheReconciler.Reconcile(Index, names);

        int parts = 0;
        foreach (string part in result.PartFiles)
        {
            if (TryDeleteOwnedFile(part))
            {
                parts++;
            }
        }

        Index = result.Index;
        Log.Info($"reconcile dropped={result.DroppedIds.Count} parts={parts}");

        if (result.DroppedIds.Count > 0 || parts > 0 || Index.Applied.Count != appliedBefore)
        {
            Save();
        }
    }

    public CachedImage? TryGet(ImageId id, string resolution) =>
        Index.Images.FirstOrDefault(i =>
            string.Equals(i.Id, id.Value, StringComparison.Ordinal)
            && string.Equals(i.Resolution, resolution, StringComparison.Ordinal));

    /// <summary>
    /// <c>{date}_{Name}_{MARKET}{digits}.jpg</c> for UHD, <c>{date}_{Name}_{MARKET}{digits}.{w}x{h}.jpg</c> otherwise
    /// (see <see cref="CacheFileName"/>).
    /// </summary>
    public string FileNameFor(CatalogEntry entry, string resolution, DateTimeOffset nowUtc) =>
        CacheFileName.For(entry, resolution, nowUtc);

    /// <summary>
    /// Returns the cached entry for <c>(id, resolution)</c> without any network call when it exists and its file is
    /// present (SRC-09); otherwise downloads it from <see cref="BingImageUrl.PrimaryHost"/> and records it via
    /// <see cref="Add"/>. Retries and the single www→cn failover are owned by <see cref="HttpGateway"/> (SRC-06);
    /// this method issues exactly one download so a Bing outage costs one policy run per host, not three. Returns
    /// null when the download is rejected; nothing is written to the index in that case.
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

        DateTimeOffset now = _time.GetUtcNow();
        string fileName = FileNameFor(entry, resolution, now);
        string finalPath = Path.Combine(_cacheDir, fileName);
        (int minW, int minH) = BingImageUrl.MinDimensions(resolution);
        bool isUhd = string.Equals(resolution, "UHD", StringComparison.Ordinal);

        Uri url = isUhd
            ? BingImageUrl.Image(BingImageUrl.PrimaryHost, entry.Id, null, null)
            : BingImageUrl.Image(BingImageUrl.PrimaryHost, entry.Id, minW, minH);

        DownloadResult result = await http.DownloadJpegAsync(url, finalPath, minW, minH, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            Log.Info($"download rejected host={url.Host} reason={result.Reason}");
            return null;
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
            DownloadedUtc = _time.GetUtcNow(),
        };

        try
        {
            Add(cached);
        }
        catch
        {
            // The index could not record the download (IN-09): the bytes are unowned, so remove them rather than
            // leave an orphan the reconcile pass will never touch — unless an entry still on the index names that
            // very file (a replaced entry with the same date prefix), in which case it still owns them.
            if (!IsFileNamedByIndex(fileName))
            {
                TryDeleteOwnedFile(fileName);
            }

            throw;
        }

        Log.Info($"cache add id={cached.Id} file={cached.File} bytes={cached.Bytes} dims={cached.Width}x{cached.Height}");
        return cached;
    }

    /// <summary>
    /// Appends <paramref name="image"/> (replacing any entry with the same id + resolution), stamps it with the next
    /// <see cref="CachedImage.Seq"/> so cache order never depends on the wall clock (WR-01), evicts down to
    /// <see cref="MaxImages"/> with <c>Applied ∪ {image.Id}</c> protected, saves the index exactly once, and only
    /// then deletes each victim's file best-effort — unless a surviving entry still names that file, in which case
    /// the bytes stay — logging <c>evict id= file=</c> per victim. The save comes before any delete (IN-09): when
    /// <c>index.json</c> cannot be written the in-memory index is put back to what the file still says and no
    /// bytes are touched, so memory and disk never diverge and the caller sees the exception.
    /// </summary>
    public CachedImage Add(CachedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        List<CachedImage> before = Index.Images;
        long nextSeqBefore = Index.NextSeq;
        List<CachedImage> images = [.. before];

        List<CachedImage> replaced = images.FindAll(i =>
            string.Equals(i.Id, image.Id, StringComparison.Ordinal)
            && string.Equals(i.Resolution, image.Resolution, StringComparison.Ordinal));
        images.RemoveAll(replaced.Contains);
        image.Seq = Index.NextSeq++;
        images.Add(image);

        var protectedIds = new HashSet<string>(Index.Applied, StringComparer.Ordinal) { image.Id };
        IReadOnlyList<CachedImage> victims = EvictionPolicy.SelectVictims(images, protectedIds, MaxImages);
        foreach (CachedImage victim in victims)
        {
            images.Remove(victim);
        }

        Index.Images = images;
        try
        {
            Save();
        }
        catch
        {
            Index.Images = before;
            Index.NextSeq = nextSeqBefore;
            image.Seq = 0;
            throw;
        }

        // A stale file left by a replaced entry (different date prefix, same id + resolution) is owned by the index
        // and is deleted like a victim — unless the id is applied, in which case the file on the desktop stays.
        foreach (CachedImage old in replaced)
        {
            bool sameFile = string.Equals(old.File, image.File, StringComparison.OrdinalIgnoreCase);
            bool applied = Index.Applied.Contains(old.Id, StringComparer.Ordinal);
            if (!sameFile && !applied && !IsFileNamedByIndex(old.File))
            {
                DeleteVictimFile(old);
            }
        }

        // A victim's file is deleted only when no surviving entry names it. File names are unique per ID by
        // construction now, but an index written by an earlier build (or a hand-edited one) may still have two
        // entries sharing a file — and one of them may be the applied image.
        foreach (CachedImage victim in victims)
        {
            if (IsFileNamedByIndex(victim.File))
            {
                Log.Info($"evict id={victim.Id} file={victim.File} kept=shared");
                continue;
            }

            DeleteVictimFile(victim);
        }

        return image;
    }

    private bool IsFileNamedByIndex(string file) =>
        Index.Images.Any(i => string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Records the image about to be set on the desktop (single entry in Phase 1) and saves. Call it BEFORE the
    /// desktop changes so the eviction protection is on disk first (WR-07); <see cref="RestoreApplied"/> undoes it
    /// when the apply does not happen.
    /// </summary>
    public void MarkApplied(ImageId id)
    {
        Index.Applied = [id.Value];
        Save();
    }

    /// <summary>Puts back an earlier <see cref="CacheIndex.Applied"/> set (the desktop still shows it) and saves.</summary>
    public void RestoreApplied(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Index.Applied = [.. ids];
        Save();
    }

    public void Save() => AtomicJsonFile.Save(_indexPath, Index, CoreJsonContext.Default.CacheIndex);

    private void DeleteVictimFile(CachedImage victim)
    {
        Log.Info($"evict id={victim.Id} file={victim.File}");
        TryDeleteOwnedFile(victim.File);
    }

    /// <summary>
    /// Deletes a file by bare name inside the cache directory, best-effort. Refuses anything that is not a bare
    /// name (a hand-edited index cannot make the app delete outside its own directory).
    /// </summary>
    private bool TryDeleteOwnedFile(string fileName)
    {
        if (!CacheReconciler.IsBareFileName(fileName))
        {
            Log.Warn($"delete refused name={fileName}");
            return false;
        }

        string path = Path.Combine(_cacheDir, fileName);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (IOException ex)
        {
            Log.Warn($"delete failed file={fileName}", ex);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn($"delete failed file={fileName}", ex);
            return false;
        }
    }
}
