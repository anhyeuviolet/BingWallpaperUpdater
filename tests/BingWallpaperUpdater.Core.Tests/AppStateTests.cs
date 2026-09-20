using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// ROT-07 / D-08: <c>state.json</c> compatibility. A Phase 1 file loads with the scheduler fields null and no schema
/// bump; every field round-trips; null fields are omitted on write. No LogSink needed (nothing is logged).
/// </summary>
public sealed class AppStateTests : IDisposable
{
    private const string ImageId = "OHR.AlphornBavaria_EN-US6200857270";

    private readonly string _dir;

    public AppStateTests()
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

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void LoadOrCreate_Phase1Shape_NewFieldsNull_SchemaVersion1()
    {
        string path = PathFor("state.json");
        const string body = """
            {
              "schemaVersion": 1,
              "catalogEtag": "W/abc",
              "catalogFetchedUtc": "2026-09-20T02:51:00.0000000+00:00",
              "currentImageId": "OHR.AlphornBavaria_EN-US6200857270",
              "lastAppliedUtc": "2026-09-20T02:51:01.0000000+00:00"
            }
            """;
        File.WriteAllText(path, body);

        AppState state = AppState.LoadOrCreate(path);

        Assert.Equal(1, state.SchemaVersion);
        Assert.Equal("W/abc", state.CatalogEtag);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 2, 51, 0, TimeSpan.Zero), state.CatalogFetchedUtc);
        Assert.Equal(ImageId, state.CurrentImageId);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 2, 51, 1, TimeSpan.Zero), state.LastAppliedUtc);
        Assert.Null(state.NextDueUtc);
        Assert.Null(state.LastCheckUtc);
        Assert.Null(state.LastSeenNewestId);
    }

    [Fact]
    public void LoadOrCreate_MissingFile_FreshStateWithEverythingNull()
    {
        AppState state = AppState.LoadOrCreate(PathFor("missing.json"));

        Assert.Equal(1, state.SchemaVersion);
        Assert.Null(state.CurrentImageId);
        Assert.Null(state.NextDueUtc);
        Assert.Null(state.LastCheckUtc);
        Assert.Null(state.LastSeenNewestId);
        Assert.False(File.Exists(PathFor("missing.json")));   // no write-back needed for state
    }

    [Fact]
    public void SaveLoad_AllEightFields_RoundTrip_SecondSaveByteIdentical()
    {
        var original = new AppState
        {
            SchemaVersion = 1,
            CatalogEtag = "\"88f5d306\"",
            CatalogFetchedUtc = new DateTimeOffset(2026, 9, 20, 8, 7, 45, 566, TimeSpan.Zero),
            CurrentImageId = ImageId,
            LastAppliedUtc = new DateTimeOffset(2026, 9, 20, 15, 7, 45, 334, TimeSpan.FromHours(7)),   // +07:00 offset survives
            NextDueUtc = new DateTimeOffset(2026, 9, 20, 8, 37, 45, 334, TimeSpan.Zero),
            LastCheckUtc = new DateTimeOffset(2026, 9, 20, 8, 7, 45, 334, TimeSpan.Zero),
            LastSeenNewestId = "OHR.ParisSunset_EN-US6532307523",
        };
        string first = PathFor("state.json");
        string second = PathFor("state2.json");

        original.Save(first);
        AppState? loaded = AtomicJsonFile.Load(first, CoreJsonContext.Default.AppState);

        Assert.NotNull(loaded);
        Assert.Equal(original.SchemaVersion, loaded!.SchemaVersion);
        Assert.Equal(original.CatalogEtag, loaded.CatalogEtag);
        Assert.Equal(original.CatalogFetchedUtc, loaded.CatalogFetchedUtc);
        Assert.Equal(original.CurrentImageId, loaded.CurrentImageId);
        Assert.Equal(original.LastAppliedUtc, loaded.LastAppliedUtc);
        Assert.Equal(original.LastAppliedUtc!.Value.Offset, loaded.LastAppliedUtc!.Value.Offset);
        Assert.Equal(original.NextDueUtc, loaded.NextDueUtc);
        Assert.Equal(original.LastCheckUtc, loaded.LastCheckUtc);
        Assert.Equal(original.LastSeenNewestId, loaded.LastSeenNewestId);

        loaded.Save(second);
        Assert.Equal(File.ReadAllText(first), File.ReadAllText(second));
    }

    [Fact]
    public void Save_NullFields_Omitted()
    {
        string path = PathFor("state.json");

        new AppState().Save(path);
        string text = File.ReadAllText(path);

        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.DoesNotContain("nextDueUtc", text);
        Assert.DoesNotContain("lastCheckUtc", text);
        Assert.DoesNotContain("lastSeenNewestId", text);
        Assert.DoesNotContain("currentImageId", text);
    }

    [Fact]
    public void Save_ScheduleFields_UseCamelCaseNames()
    {
        string path = PathFor("state.json");
        var state = new AppState
        {
            NextDueUtc = new DateTimeOffset(2026, 9, 20, 8, 37, 45, TimeSpan.Zero),
            LastCheckUtc = new DateTimeOffset(2026, 9, 20, 8, 7, 45, TimeSpan.Zero),
            LastSeenNewestId = ImageId,
        };

        state.Save(path);
        string text = File.ReadAllText(path);

        Assert.Contains("\"nextDueUtc\": \"2026-09-20T08:37:45+00:00\"", text);
        Assert.Contains("\"lastCheckUtc\": \"2026-09-20T08:07:45+00:00\"", text);
        Assert.Contains($"\"lastSeenNewestId\": \"{ImageId}\"", text);
    }
}
