using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// SRC-09: a cached (id, resolution) is served without any network call. The handler throws on any use, so the
/// only way a "miss" can be observed is the exception reaching the caller. Joins the "LogSink" collection because
/// the hit test asserts on the process-global <see cref="Log"/>.
/// </summary>
[Collection("LogSink")]
public sealed class ImageCacheEnsureTests : IDisposable
{
    private const string Id = "OHR.AlphornBavaria_EN-US6200857270";

    private readonly string _dir;
    private readonly string _indexPath;
    private readonly string _logPath;
    private readonly HttpGateway _http;

    public ImageCacheEnsureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bwu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "index.json");
        _logPath = Path.Combine(_dir, "log.txt");
        Log.Initialize(_logPath);
        // DownloadRoot = the temp cache so a miss reaches the handler instead of the "outside cache directory" guard.
        _http = new HttpGateway(new ThrowingHandler()) { DownloadRoot = _dir };
    }

    public void Dispose()
    {
        _http.Dispose();
        Log.Initialize(Path.Combine(_dir, "closed.log"));
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Any request is a test failure: the network must not be used.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("network must not be used");
    }

    private string LogText() => File.Exists(_logPath) ? File.ReadAllText(_logPath) : string.Empty;

    private static CatalogEntry Entry(string id = Id)
    {
        Assert.True(ImageId.TryParse(id, out ImageId parsed));
        return new CatalogEntry(parsed, "2026-09-20", "20260919", "The Alpine sound of Oktoberfest", null, null, CatalogSources.GitHub);
    }

    private static CachedImage Cached(string id, string resolution, string file) => new()
    {
        Id = id,
        Market = "EN-US",
        Date = "2026-09-20",
        StartDate = "20260919",
        Resolution = resolution,
        Width = 3840,
        Height = 2160,
        File = file,
        Bytes = 16,
        SourceUrl = $"https://www.bing.com/th?id={id}_UHD.jpg",
        DownloadedUtc = new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero),
    };

    /// <summary>Seeds index.json (via AtomicJsonFile) with one UHD entry and its dummy file; returns a loaded cache.</summary>
    private ImageCache SeededCache()
    {
        var index = new CacheIndex
        {
            Applied = [Id],
            Images = [Cached(Id, "UHD", "2026-09-20_AlphornBavaria.jpg")],
        };
        AtomicJsonFile.Save(_indexPath, index, CoreJsonContext.Default.CacheIndex);
        File.WriteAllBytes(Path.Combine(_dir, "2026-09-20_AlphornBavaria.jpg"), new byte[16]);

        var cache = new ImageCache(_dir, _indexPath);
        cache.Load();
        return cache;
    }

    [Fact]
    public async Task Ensure_CachedIdAndResolutionWithFilePresent_ReturnsTheEntryAndNeverInvokesTheHandler()
    {
        ImageCache cache = SeededCache();

        CachedImage? hit = await cache.EnsureAsync(Entry(), "UHD", _http, CancellationToken.None);

        Assert.NotNull(hit);
        Assert.Equal(Id, hit!.Id);
        Assert.Equal("2026-09-20_AlphornBavaria.jpg", hit.File);
        Assert.Contains($"cache hit id={Id}", LogText());
        Assert.DoesNotContain("download rejected", LogText());
        Assert.Single(cache.Index.Images);
    }

    [Fact]
    public async Task Ensure_SameIdDifferentResolution_IsAMissThatReachesTheHandler()
    {
        ImageCache cache = SeededCache();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.EnsureAsync(Entry(), "1920x1080", _http, CancellationToken.None));

        Assert.Equal("network must not be used", ex.Message);
        Assert.Single(cache.Index.Images); // nothing recorded for the failed resolution
        Assert.Equal("UHD", cache.Index.Images[0].Resolution);
    }

    [Fact]
    public async Task Ensure_FileDeletedAfterLoad_ReconcileDropsTheEntry_ThenEnsureIsAMiss()
    {
        ImageCache cache = SeededCache();
        Assert.NotNull(cache.TryGet(Entry().Id, "UHD"));

        File.Delete(Path.Combine(_dir, "2026-09-20_AlphornBavaria.jpg"));
        cache.Reconcile();

        Assert.Null(cache.TryGet(Entry().Id, "UHD"));
        Assert.Empty(cache.Index.Images);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.EnsureAsync(Entry(), "UHD", _http, CancellationToken.None));
    }

    [Fact]
    public async Task Ensure_EntryListedButFileMissing_IsAMissEvenWithoutReconcile()
    {
        ImageCache cache = SeededCache();
        File.Delete(Path.Combine(_dir, "2026-09-20_AlphornBavaria.jpg"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.EnsureAsync(Entry(), "UHD", _http, CancellationToken.None));
    }

    [Fact]
    public async Task Ensure_UnknownId_IsAMiss()
    {
        ImageCache cache = SeededCache();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.EnsureAsync(Entry("OHR.SomethingElse_EN-US1"), "UHD", _http, CancellationToken.None));
    }

    [Fact]
    public void ThrowingHandler_ThrowsOnAnyUse()
    {
        using var client = new HttpClient(new ThrowingHandler());

        Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://www.bing.com/")).GetAwaiter().GetResult();
    }
}
