using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// CACHE-05: the pure <see cref="CacheReconciler"/>, corrupt / hand-edited index loading, and
/// <see cref="ImageCache.Reconcile"/> on a real temp directory. Joins the "LogSink" collection because the
/// directory test asserts on the process-global <see cref="Log"/>.
/// </summary>
[Collection("LogSink")]
public sealed class ImageCacheReconcileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public ImageCacheReconcileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bwu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _logPath = Path.Combine(_dir, "log.txt");
        Log.Initialize(_logPath);
    }

    public void Dispose()
    {
        Log.Initialize(Path.Combine(_dir, "closed.log"));
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LogText() => File.Exists(_logPath) ? File.ReadAllText(_logPath) : string.Empty;

    private static CachedImage Image(int n) => new()
    {
        Id = $"OHR.Image{n}_EN-US{n}",
        Market = "EN-US",
        Date = "2026-09-01",
        Resolution = "UHD",
        Width = 3840,
        Height = 2160,
        File = $"2026-09-01_Image{n}.jpg",
        Bytes = 16,
        SourceUrl = $"https://www.bing.com/th?id=OHR.Image{n}_EN-US{n}_UHD.jpg",
        DownloadedUtc = new DateTimeOffset(2026, 9, 1, 0, n, 0, TimeSpan.Zero),
    };

    private static CacheIndex ThreeEntries() => new()
    {
        Applied = ["OHR.Image1_EN-US1"],
        Images = [Image(1), Image(2), Image(3)],
    };

    // ---- pure reconciler --------------------------------------------------------------------------------

    [Fact]
    public void Reconcile_MissingFile_IsDroppedFromTheIndexAndReported()
    {
        string[] files = ["2026-09-01_Image1.jpg", "2026-09-01_Image3.jpg", "x.jpg.part", "user-note.txt"];

        ReconcileResult result = CacheReconciler.Reconcile(ThreeEntries(), files);

        Assert.Equal(2, result.Index.Images.Count);
        Assert.Equal(["OHR.Image2_EN-US2"], result.DroppedIds);
        Assert.DoesNotContain(result.Index.Images, i => i.Id == "OHR.Image2_EN-US2");
    }

    [Fact]
    public void Reconcile_PartFile_IsListedForDeletion()
    {
        string[] files = ["2026-09-01_Image1.jpg", "2026-09-01_Image3.jpg", "x.jpg.part", "user-note.txt"];

        ReconcileResult result = CacheReconciler.Reconcile(ThreeEntries(), files);

        Assert.Equal(["x.jpg.part"], result.PartFiles);
    }

    [Fact]
    public void Reconcile_UnknownFile_IsNeitherDroppedNorListedForDeletion()
    {
        string[] files = ["2026-09-01_Image1.jpg", "2026-09-01_Image3.jpg", "x.jpg.part", "user-note.txt", "holiday.jpg"];

        ReconcileResult result = CacheReconciler.Reconcile(ThreeEntries(), files);

        Assert.DoesNotContain("user-note.txt", result.PartFiles);
        Assert.DoesNotContain("holiday.jpg", result.PartFiles);
        Assert.DoesNotContain("user-note.txt", result.DroppedIds);
        Assert.DoesNotContain("holiday.jpg", result.DroppedIds);
    }

    [Fact]
    public void Reconcile_NullIndex_YieldsEmptyIndexWithNoDrops()
    {
        ReconcileResult result = CacheReconciler.Reconcile(null, ["x.jpg.part", "2026-09-01_Image1.jpg"]);

        Assert.Empty(result.Index.Images);
        Assert.Empty(result.Index.Applied);
        Assert.Empty(result.DroppedIds);
        Assert.Equal(["x.jpg.part"], result.PartFiles);
    }

    [Fact]
    public void Reconcile_AllFilesPresent_KeepsEveryEntryAndTheAppliedId()
    {
        string[] files = ["2026-09-01_IMAGE1.JPG", "2026-09-01_Image2.jpg", "2026-09-01_Image3.jpg"];

        ReconcileResult result = CacheReconciler.Reconcile(ThreeEntries(), files);

        Assert.Equal(3, result.Index.Images.Count);
        Assert.Empty(result.DroppedIds);
        Assert.Equal(["OHR.Image1_EN-US1"], result.Index.Applied);
    }

    [Fact]
    public void Reconcile_AppliedIdWithoutEntry_IsDroppedFromApplied()
    {
        CacheIndex index = ThreeEntries();
        index.Applied = ["OHR.Image1_EN-US1", "OHR.Gone_EN-US9"];

        ReconcileResult result = CacheReconciler.Reconcile(index, ["2026-09-01_Image1.jpg", "2026-09-01_Image2.jpg", "2026-09-01_Image3.jpg"]);

        Assert.Equal(["OHR.Image1_EN-US1"], result.Index.Applied);
    }

    [Fact]
    public void Reconcile_EntryWhoseFileIsNotABareName_IsDropped()
    {
        CacheIndex index = ThreeEntries();
        index.Images[1].File = @"..\..\evil.jpg";

        ReconcileResult result = CacheReconciler.Reconcile(index, ["2026-09-01_Image1.jpg", "2026-09-01_Image3.jpg", "evil.jpg"]);

        Assert.Contains("OHR.Image2_EN-US2", result.DroppedIds);
        Assert.Equal(2, result.Index.Images.Count);
    }

    // ---- loading a hand-edited index -----------------------------------------------------------------

    [Fact]
    public void Load_CorruptIndex_YieldsEmptyIndexWithoutThrowing()
    {
        string indexPath = Path.Combine(_dir, "index.json");
        File.WriteAllText(indexPath, "{ not json");
        var cache = new ImageCache(_dir, indexPath);

        Exception? ex = Record.Exception(cache.Load);

        Assert.Null(ex);
        Assert.Empty(cache.Index.Images);
        Assert.Empty(cache.Index.Applied);
    }

    [Fact]
    public void Load_IndexWithUnknownExtraProperties_YieldsTheKnownEntries()
    {
        string indexPath = Path.Combine(_dir, "index.json");
        File.WriteAllText(indexPath, """
            {
              "schemaVersion": 1,
              "futureFlag": true,
              "applied": ["OHR.Image1_EN-US1"],
              "images": [
                { "id": "OHR.Image1_EN-US1", "market": "EN-US", "resolution": "UHD", "file": "2026-09-01_Image1.jpg", "bytes": 16, "sha256": "abc", "downloadedUtc": "2026-09-01T00:01:00+00:00" }
              ]
            }
            """);
        var cache = new ImageCache(_dir, indexPath);

        cache.Load();

        Assert.Single(cache.Index.Images);
        Assert.Equal("OHR.Image1_EN-US1", cache.Index.Images[0].Id);
        Assert.Null(cache.Index.Images[0].Title);
        Assert.Equal(["OHR.Image1_EN-US1"], cache.Index.Applied);
    }

    // ---- ImageCache.Reconcile on a real directory ---------------------------------------------------

    [Fact]
    public void ImageCacheReconcile_DropsMissingEntry_DeletesPart_LeavesUnrelatedFiles_AndLogs()
    {
        string indexPath = Path.Combine(_dir, "index.json");
        CacheIndex index = ThreeEntries();
        AtomicJsonFile.Save(indexPath, index, CoreJsonContext.Default.CacheIndex);
        File.WriteAllBytes(Path.Combine(_dir, "2026-09-01_Image1.jpg"), new byte[16]);
        // Image2's file is deliberately missing (hand-deleted).
        File.WriteAllBytes(Path.Combine(_dir, "2026-09-01_Image3.jpg"), new byte[16]);
        File.WriteAllBytes(Path.Combine(_dir, "2026-09-02_Stray.jpg.part"), new byte[8]);
        File.WriteAllText(Path.Combine(_dir, "user-note.txt"), "mine");
        File.WriteAllBytes(Path.Combine(_dir, "holiday.jpg"), new byte[16]);

        var cache = new ImageCache(_dir, indexPath);
        cache.Reconcile();

        Assert.Equal(2, cache.Index.Images.Count);
        Assert.DoesNotContain(cache.Index.Images, i => i.Id == "OHR.Image2_EN-US2");
        Assert.False(File.Exists(Path.Combine(_dir, "2026-09-02_Stray.jpg.part")), ".part must be deleted");
        Assert.True(File.Exists(Path.Combine(_dir, "user-note.txt")), "unrelated file must stay");
        Assert.True(File.Exists(Path.Combine(_dir, "holiday.jpg")), "unknown jpg must stay");
        Assert.True(File.Exists(Path.Combine(_dir, "2026-09-01_Image1.jpg")));
        Assert.True(File.Exists(Path.Combine(_dir, "2026-09-01_Image3.jpg")));
        Assert.False(File.Exists(indexPath + ".tmp"));

        CacheIndex? onDisk = AtomicJsonFile.Load(indexPath, CoreJsonContext.Default.CacheIndex);
        Assert.NotNull(onDisk);
        Assert.Equal(2, onDisk!.Images.Count);
        Assert.DoesNotContain(onDisk.Images, i => i.Id == "OHR.Image2_EN-US2");
        Assert.Contains("reconcile dropped=1 parts=1", LogText());
    }

    [Fact]
    public void ImageCacheReconcile_MissingDirectoryAndIndex_YieldsEmptyIndexWithoutThrowing()
    {
        string missingDir = Path.Combine(_dir, "does-not-exist");
        var cache = new ImageCache(missingDir, Path.Combine(missingDir, "index.json"));

        Exception? ex = Record.Exception(cache.Reconcile);

        Assert.Null(ex);
        Assert.Empty(cache.Index.Images);
        Assert.Contains("reconcile dropped=0 parts=0", LogText());
    }
}
