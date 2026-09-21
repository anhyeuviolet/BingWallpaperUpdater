using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// CACHE-01 / CACHE-02: the pure <see cref="EvictionPolicy"/> and its wiring into <see cref="ImageCache.Add"/>.
/// Joins the "LogSink" collection because the Add test asserts on the process-global <see cref="Log"/>.
/// </summary>
[Collection("LogSink")]
public sealed class ImageCacheEvictionTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir;
    private readonly string _logPath;

    public ImageCacheEvictionTests()
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

    /// <summary>Image number <paramref name="n"/> downloaded <paramref name="minutesAfterT0"/> minutes after T0.</summary>
    private static CachedImage Image(int n, double minutesAfterT0) => new()
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
        DownloadedUtc = T0.AddMinutes(minutesAfterT0),
    };

    /// <summary>Images 1..count with strictly increasing download time (image 1 is the oldest).</summary>
    private static List<CachedImage> Ascending(int count) =>
        Enumerable.Range(1, count).Select(n => Image(n, n)).ToList();

    private static HashSet<string> Ids(params string[] ids) => new(ids, StringComparer.Ordinal);

    // ---- pure policy ----------------------------------------------------------------------------------

    [Fact]
    public void SelectVictims_ElevenImages_NoProtected_ReturnsTheOldestOnly()
    {
        List<CachedImage> images = Ascending(11);

        IReadOnlyList<CachedImage> victims = EvictionPolicy.SelectVictims(images, Ids(), ImageCache.MaxImages);

        Assert.Single(victims);
        Assert.Equal("OHR.Image1_EN-US1", victims[0].Id);
    }

    [Fact]
    public void SelectVictims_ElevenImages_AppliedIsOldest_ReturnsSecondOldestAndNeverTheApplied()
    {
        List<CachedImage> images = Ascending(11);
        HashSet<string> applied = Ids("OHR.Image1_EN-US1");

        IReadOnlyList<CachedImage> victims = EvictionPolicy.SelectVictims(images, applied, ImageCache.MaxImages);

        Assert.Single(victims);
        Assert.Equal("OHR.Image2_EN-US2", victims[0].Id);
        Assert.DoesNotContain(victims, v => v.Id == "OHR.Image1_EN-US1");
    }

    [Fact]
    public void SelectVictims_ElevenImages_JustAddedHasOldestTimestamp_ReturnsOldestOfTheOthers()
    {
        // First run: nothing applied; the just-added image carries the oldest timestamp (clock skew).
        List<CachedImage> images = Ascending(10);
        CachedImage justAdded = Image(11, -5);
        images.Add(justAdded);

        IReadOnlyList<CachedImage> victims = EvictionPolicy.SelectVictims(images, Ids(justAdded.Id), ImageCache.MaxImages);

        Assert.Single(victims);
        Assert.Equal("OHR.Image1_EN-US1", victims[0].Id);
    }

    [Fact]
    public void SelectVictims_TwelveImages_TieOnOldestTimestamp_ReturnsIndexOrderAndIsStable()
    {
        List<CachedImage> images = Ascending(10);
        // Two extra images that share the oldest timestamp, inserted at index 3 and 7.
        CachedImage tieA = Image(11, 0);
        CachedImage tieB = Image(12, 0);
        images.Insert(3, tieA);
        images.Insert(7, tieB);

        IReadOnlyList<CachedImage> first = EvictionPolicy.SelectVictims(images, Ids(), ImageCache.MaxImages);
        IReadOnlyList<CachedImage> second = EvictionPolicy.SelectVictims(images, Ids(), ImageCache.MaxImages);

        Assert.Equal(new[] { tieA.Id, tieB.Id }, first.Select(v => v.Id).ToArray());
        Assert.Equal(first.Select(v => v.Id).ToArray(), second.Select(v => v.Id).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public void SelectVictims_TenOrFewerImages_ReturnsEmpty(int count)
    {
        IReadOnlyList<CachedImage> victims = EvictionPolicy.SelectVictims(Ascending(count), Ids(), ImageCache.MaxImages);

        Assert.Empty(victims);
    }

    [Fact]
    public void SelectVictims_ThirteenImages_ReturnsThreeVictimsInAscendingDownloadOrder()
    {
        IReadOnlyList<CachedImage> victims = EvictionPolicy.SelectVictims(Ascending(13), Ids(), ImageCache.MaxImages);

        Assert.Equal(new[] { "OHR.Image1_EN-US1", "OHR.Image2_EN-US2", "OHR.Image3_EN-US3" }, victims.Select(v => v.Id).ToArray());
    }

    [Fact]
    public void SelectVictims_EveryImageProtected_ReturnsEmptyEvenAboveTheCap()
    {
        List<CachedImage> images = Ascending(12);
        HashSet<string> all = Ids(images.Select(i => i.Id).ToArray());

        IReadOnlyList<CachedImage> victims = EvictionPolicy.SelectVictims(images, all, ImageCache.MaxImages);

        Assert.Empty(victims);
    }

    // ---- ImageCache.Add on real files -------------------------------------------------------------------

    [Fact]
    public void Add_EleventhImage_EvictsOldestUnprotectedFile_KeepsApplied_SavesAtomically_AndLogs()
    {
        List<CachedImage> ten = Ascending(10);
        foreach (CachedImage image in ten)
        {
            File.WriteAllBytes(Path.Combine(_dir, image.File), new byte[16]);
        }

        string indexPath = Path.Combine(_dir, "index.json");
        var index = new CacheIndex { Applied = ["OHR.Image1_EN-US1"], Images = ten };
        AtomicJsonFile.Save(indexPath, index, CoreJsonContext.Default.CacheIndex);

        var cache = new ImageCache(_dir, indexPath);
        cache.Load();
        CachedImage eleventh = Image(11, 11);
        File.WriteAllBytes(Path.Combine(_dir, eleventh.File), new byte[16]);

        CachedImage returned = cache.Add(eleventh);

        Assert.Same(eleventh, returned);
        string[] jpgs = Directory.GetFiles(_dir, "*.jpg").Select(Path.GetFileName).Select(f => f!).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(10, jpgs.Length);
        Assert.Equal(10, cache.Index.Images.Count);
        Assert.False(File.Exists(Path.Combine(_dir, "2026-09-01_Image2.jpg")), "the oldest unprotected image (2) must be evicted");
        Assert.True(File.Exists(Path.Combine(_dir, "2026-09-01_Image1.jpg")), "the applied image must stay on disk");
        Assert.True(File.Exists(Path.Combine(_dir, eleventh.File)), "the just-added image must stay on disk");
        Assert.DoesNotContain(cache.Index.Images, i => i.Id == "OHR.Image2_EN-US2");
        Assert.Contains(cache.Index.Images, i => i.Id == "OHR.Image1_EN-US1");
        Assert.Contains(cache.Index.Images, i => i.Id == eleventh.Id);

        Assert.False(File.Exists(indexPath + ".tmp"), "no index.json.tmp may remain after Save");
        CacheIndex? reloaded = AtomicJsonFile.Load(indexPath, CoreJsonContext.Default.CacheIndex);
        Assert.NotNull(reloaded);
        Assert.Equal(10, reloaded!.Images.Count);
        Assert.Equal(["OHR.Image1_EN-US1"], reloaded.Applied);
        Assert.Contains("evict id=OHR.Image2_EN-US2", LogText());
    }

    /// <summary>
    /// WALL-03 / CACHE-02 invariant (Plan 03-04 add-alongside): every ID in <c>Applied</c> — one per monitor in
    /// per-monitor mode — is protected, so the eviction skips both applied images and takes the oldest unprotected one.
    /// </summary>
    [Fact]
    public void Add_EleventhImage_TwoAppliedIds_EvictsOldestUnprotected_KeepsBoth()
    {
        List<CachedImage> ten = Ascending(10);
        foreach (CachedImage image in ten)
        {
            File.WriteAllBytes(Path.Combine(_dir, image.File), new byte[16]);
        }

        string indexPath = Path.Combine(_dir, "index.json");
        var index = new CacheIndex { Applied = ["OHR.Image1_EN-US1", "OHR.Image2_EN-US2"], Images = ten };
        AtomicJsonFile.Save(indexPath, index, CoreJsonContext.Default.CacheIndex);

        var cache = new ImageCache(_dir, indexPath);
        cache.Load();
        CachedImage eleventh = Image(11, 11);
        File.WriteAllBytes(Path.Combine(_dir, eleventh.File), new byte[16]);

        cache.Add(eleventh);

        Assert.Equal(10, cache.Index.Images.Count);
        Assert.False(File.Exists(Path.Combine(_dir, "2026-09-01_Image3.jpg")), "the third-oldest image is the oldest unprotected one");
        Assert.True(File.Exists(Path.Combine(_dir, "2026-09-01_Image1.jpg")), "the first applied image must stay on disk");
        Assert.True(File.Exists(Path.Combine(_dir, "2026-09-01_Image2.jpg")), "the second applied image must stay on disk");
        Assert.DoesNotContain(cache.Index.Images, i => i.Id == "OHR.Image3_EN-US3");
        Assert.Contains(cache.Index.Images, i => i.Id == "OHR.Image1_EN-US1");
        Assert.Contains(cache.Index.Images, i => i.Id == "OHR.Image2_EN-US2");
        Assert.Contains(cache.Index.Images, i => i.Id == eleventh.Id);

        CacheIndex? reloaded = AtomicJsonFile.Load(indexPath, CoreJsonContext.Default.CacheIndex);
        Assert.NotNull(reloaded);
        Assert.Equal(["OHR.Image1_EN-US1", "OHR.Image2_EN-US2"], reloaded!.Applied);
        Assert.Contains("evict id=OHR.Image3_EN-US3", LogText());
        Assert.DoesNotContain("evict id=OHR.Image1_EN-US1", LogText());
        Assert.DoesNotContain("evict id=OHR.Image2_EN-US2", LogText());
    }

    /// <summary>
    /// IN-09: the index is saved before any victim file is deleted. When the save fails, the in-memory index is put
    /// back to what index.json still says, every file stays, and the caller sees the exception.
    /// </summary>
    [Fact]
    public void Add_SaveThrows_RestoresMemoryToDisk_DeletesNothing_AndRethrows()
    {
        List<CachedImage> ten = Ascending(10);
        foreach (CachedImage image in ten)
        {
            File.WriteAllBytes(Path.Combine(_dir, image.File), new byte[16]);
        }

        string indexPath = Path.Combine(_dir, "index.json");
        AtomicJsonFile.Save(indexPath, new CacheIndex { Applied = ["OHR.Image1_EN-US1"], Images = ten }, CoreJsonContext.Default.CacheIndex);
        var cache = new ImageCache(_dir, indexPath);
        cache.Load();
        long nextSeqBefore = cache.Index.NextSeq;
        CachedImage eleventh = Image(11, 11);
        File.WriteAllBytes(Path.Combine(_dir, eleventh.File), new byte[16]);
        Directory.CreateDirectory(indexPath + ".tmp");   // AtomicJsonFile.Save opens <path>.tmp with FileMode.Create -> throws

        Assert.ThrowsAny<Exception>(() => cache.Add(eleventh));

        Assert.Equal(10, cache.Index.Images.Count);
        Assert.DoesNotContain(cache.Index.Images, i => i.Id == eleventh.Id);
        Assert.Contains(cache.Index.Images, i => i.Id == "OHR.Image2_EN-US2");   // the would-be victim is still listed
        Assert.True(File.Exists(Path.Combine(_dir, "2026-09-01_Image2.jpg")), "no file may be deleted when the index was not saved");
        Assert.Equal(nextSeqBefore, cache.Index.NextSeq);
        Assert.Equal(0, eleventh.Seq);
        Assert.DoesNotContain("evict id=", LogText());

        CacheIndex onDisk = AtomicJsonFile.Load(indexPath, CoreJsonContext.Default.CacheIndex)!;
        Assert.Equal(cache.Index.Images.Select(i => i.Id), onDisk.Images.Select(i => i.Id));   // memory == disk
    }

    /// <summary>
    /// WR-01: an index from an earlier build can hold two IDs that share one file (the old naming dropped market and
    /// suffix). Evicting one of them must not delete the bytes the other — here the applied image — still points at.
    /// </summary>
    [Fact]
    public void Add_VictimSharesItsFileWithASurvivingEntry_FileIsKeptAndTheSkipIsLogged()
    {
        List<CachedImage> ten = Ascending(10);
        // Image 2 (oldest unprotected) and image 10 (applied) both name the same legacy-style file.
        const string shared = "2026-09-01_Shared.jpg";
        ten[1].File = shared;
        ten[9].File = shared;
        foreach (CachedImage image in ten)
        {
            File.WriteAllBytes(Path.Combine(_dir, image.File), new byte[16]);
        }

        string indexPath = Path.Combine(_dir, "index.json");
        AtomicJsonFile.Save(indexPath, new CacheIndex { Applied = [ten[9].Id], Images = ten }, CoreJsonContext.Default.CacheIndex);
        var cache = new ImageCache(_dir, indexPath);
        cache.Load();
        CachedImage eleventh = Image(11, 11);
        File.WriteAllBytes(Path.Combine(_dir, eleventh.File), new byte[16]);

        cache.Add(eleventh);

        Assert.DoesNotContain(cache.Index.Images, i => i.Id == "OHR.Image1_EN-US1");
        Assert.False(File.Exists(Path.Combine(_dir, "2026-09-01_Image1.jpg")), "the unshared oldest image is evicted normally");
        Assert.Equal(10, cache.Index.Images.Count);

        // Push one more so image 2 becomes the victim while image 10 (applied) still names the same file.
        CachedImage twelfth = Image(12, 12);
        File.WriteAllBytes(Path.Combine(_dir, twelfth.File), new byte[16]);
        cache.Add(twelfth);

        Assert.DoesNotContain(cache.Index.Images, i => i.Id == "OHR.Image2_EN-US2");
        Assert.Contains(cache.Index.Images, i => i.Id == "OHR.Image10_EN-US10" && i.File == shared);
        Assert.True(File.Exists(Path.Combine(_dir, shared)), "a file still named by a surviving entry must never be deleted");
        Assert.Contains($"evict id=OHR.Image2_EN-US2 file={shared} kept=shared", LogText());
    }

    [Fact]
    public void Add_SameIdAndResolution_ReplacesTheEntryInsteadOfDuplicating()
    {
        string indexPath = Path.Combine(_dir, "index.json");
        var cache = new ImageCache(_dir, indexPath);
        cache.Load();
        CachedImage first = Image(1, 1);
        cache.Add(first);

        CachedImage again = Image(1, 2);
        cache.Add(again);

        Assert.Single(cache.Index.Images);
        Assert.Equal(T0.AddMinutes(2), cache.Index.Images[0].DownloadedUtc);
    }
}
