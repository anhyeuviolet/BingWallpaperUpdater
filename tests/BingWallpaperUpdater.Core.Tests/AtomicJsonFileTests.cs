using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>CACHE-03 concurrency edge: tmp + rename writes, and loads that never throw.</summary>
public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string _dir;

    public AtomicJsonFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bwu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Save_WritesTheFile_LeavesNoTmp_AndRoundTripsThroughLoad()
    {
        string path = Path.Combine(_dir, "nested", "index.json");
        var index = new CacheIndex
        {
            Applied = ["OHR.Image1_EN-US1"],
            Images =
            [
                new CachedImage
                {
                    Id = "OHR.Image1_EN-US1",
                    Market = "EN-US",
                    Date = "2026-09-20",
                    StartDate = "20260919",
                    Title = "The Alpine sound of Oktoberfest",
                    Copyright = null,
                    Resolution = "UHD",
                    Width = 3840,
                    Height = 2160,
                    File = "2026-09-20_Image1.jpg",
                    Bytes = 3572288,
                    SourceUrl = "https://www.bing.com/th?id=OHR.Image1_EN-US1_UHD.jpg",
                    DownloadedUtc = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero),
                },
            ],
        };

        AtomicJsonFile.Save(path, index, CoreJsonContext.Default.CacheIndex);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        string text = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Contains("\"downloadedUtc\"", text);
        Assert.DoesNotContain("\"copyright\"", text);

        CacheIndex? loaded = AtomicJsonFile.Load(path, CoreJsonContext.Default.CacheIndex);

        Assert.NotNull(loaded);
        Assert.Equal(["OHR.Image1_EN-US1"], loaded!.Applied);
        Assert.Single(loaded.Images);
        Assert.Equal(3572288, loaded.Images[0].Bytes);
        Assert.Equal(index.Images[0].DownloadedUtc, loaded.Images[0].DownloadedUtc);
        Assert.Null(loaded.Images[0].Copyright);
    }

    [Fact]
    public void Save_OverwritesAnExistingFileAtomically()
    {
        string path = Path.Combine(_dir, "index.json");
        AtomicJsonFile.Save(path, new CacheIndex { Applied = ["a"] }, CoreJsonContext.Default.CacheIndex);

        AtomicJsonFile.Save(path, new CacheIndex { Applied = ["b"] }, CoreJsonContext.Default.CacheIndex);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(["b"], AtomicJsonFile.Load(path, CoreJsonContext.Default.CacheIndex)!.Applied);
    }

    [Fact]
    public void Load_MissingPath_ReturnsNull()
    {
        Assert.Null(AtomicJsonFile.Load(Path.Combine(_dir, "absent.json"), CoreJsonContext.Default.CacheIndex));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"string\"")]
    public void Load_CorruptFile_ReturnsNullWithoutThrowing(string content)
    {
        string path = Path.Combine(_dir, "index.json");
        File.WriteAllText(path, content);

        CacheIndex? loaded = null;
        Exception? ex = Record.Exception(() => loaded = AtomicJsonFile.Load(path, CoreJsonContext.Default.CacheIndex));

        Assert.Null(ex);
        Assert.Null(loaded);
    }
}
