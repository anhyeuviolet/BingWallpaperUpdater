using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// WR-06: <c>settings.json</c> is hand-editable, so every value is normalised after load instead of reaching the
/// pipeline raw. Joins the "LogSink" collection because the replacement log lines are asserted.
/// </summary>
[Collection("LogSink")]
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public SettingsTests()
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

    [Theory]
    [InlineData("en-US", "en-US", false)]
    [InlineData("de-DE", "de-DE", false)]
    [InlineData("EN-us", "en-US", true)]
    [InlineData(" en-US ", "en-US", true)]
    [InlineData("en-US&idx=7", "en-US", true)]
    [InlineData("en-US\u0001", "en-US", true)]
    [InlineData("en_US", "en-US", true)]
    [InlineData("english", "en-US", true)]
    [InlineData("", "en-US", true)]
    [InlineData(null, "en-US", true)]
    public void Sanitize_Market_CanonicalisesShapeOrFallsBackToDefault(string? input, string expected, bool expectChanged)
    {
        var settings = new Settings { Market = input! };

        bool changed = settings.Sanitize();

        Assert.Equal(expected, settings.Market);
        Assert.Equal(expectChanged, changed);
    }

    [Theory]
    [InlineData("UHD", "UHD", false)]
    [InlineData("1920x1080", "1920x1080", false)]
    [InlineData("1920x1200", "1920x1200", false)]
    [InlineData("uhd", "UHD", true)]
    [InlineData("1920X1080", "1920x1080", true)]
    [InlineData("4K", "UHD", true)]
    [InlineData("3840x2160", "UHD", true)]
    [InlineData("", "UHD", true)]
    [InlineData(null, "UHD", true)]
    public void Sanitize_Resolution_MatchesKnownValuesIgnoringCaseOrFallsBackToUhd(string? input, string expected, bool expectChanged)
    {
        var settings = new Settings { Resolution = input! };

        bool changed = settings.Sanitize();

        Assert.Equal(expected, settings.Resolution);
        Assert.Equal(expectChanged, changed);
    }

    /// <summary>Every value Sanitize lets through is one the download stage can act on without throwing.</summary>
    [Theory]
    [InlineData("uhd")]
    [InlineData("4K")]
    [InlineData("1920X1200")]
    [InlineData(null)]
    public void Sanitize_Resolution_AlwaysYieldsAValueMinDimensionsAccepts(string? input)
    {
        var settings = new Settings { Resolution = input! };

        settings.Sanitize();

        Exception? ex = Record.Exception(() => BingImageUrl.MinDimensions(settings.Resolution));
        Assert.Null(ex);
    }

    [Fact]
    public void Sanitize_InvalidValues_AreLoggedWithTheOffendingValueEscaped()
    {
        var settings = new Settings { Market = "en-US&idx=7", Resolution = "4K\u0007" };

        settings.Sanitize();

        Assert.Contains("settings invalid field=Market value='en-US&idx=7' using=en-US", LogText());
        Assert.Contains("settings invalid field=Resolution value='4K\\u0007' using=UHD", LogText());
    }

    [Fact]
    public void Sanitize_ValidValues_LogNothing()
    {
        var settings = new Settings { Market = "de-DE", Resolution = "1920x1080" };

        Assert.False(settings.Sanitize());
        Assert.DoesNotContain("settings invalid", LogText());
    }

    [Fact]
    public void LoadOrCreate_FileWithBadValues_ReturnsSanitizedSettingsAndLeavesTheFileAsWritten()
    {
        string path = Path.Combine(_dir, "settings.json");
        const string body = """{ "schemaVersion": 1, "market": "en-US&idx=7", "resolution": "uhd" }""";
        File.WriteAllText(path, body);

        Settings settings = Settings.LoadOrCreate(path);

        Assert.Equal("en-US", settings.Market);
        Assert.Equal("UHD", settings.Resolution);
        Assert.Equal(body, File.ReadAllText(path));
    }

    [Fact]
    public void LoadOrCreate_MissingFile_WritesDefaultsThatAreAlreadyCanonical()
    {
        string path = Path.Combine(_dir, "settings.json");

        Settings settings = Settings.LoadOrCreate(path);

        Assert.Equal(Settings.DefaultMarket, settings.Market);
        Assert.Equal(Settings.DefaultResolution, settings.Resolution);
        Assert.True(File.Exists(path));
        Assert.False(settings.Sanitize());
    }
}
