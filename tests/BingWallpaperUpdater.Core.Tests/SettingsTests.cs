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

    [Theory]
    [InlineData(30, 30, false)]
    [InlineData(60, 60, false)]
    [InlineData(120, 120, false)]
    [InlineData(240, 240, false)]
    [InlineData(480, 480, false)]
    [InlineData(1440, 1440, false)]
    [InlineData(29, 30, true)]
    [InlineData(31, 30, true)]
    [InlineData(45, 30, true)]
    [InlineData(1441, 30, true)]
    [InlineData(0, 30, true)]
    [InlineData(-30, 30, true)]
    public void Sanitize_IntervalMinutes_AllowListedValuesKept_OthersBecome30(int input, int expected, bool expectChanged)
    {
        var settings = new Settings { IntervalMinutes = input };

        bool changed = settings.Sanitize();

        Assert.Equal(expected, settings.IntervalMinutes);
        Assert.Equal(TimeSpan.FromMinutes(expected), settings.Interval);
        Assert.Equal(expectChanged, changed);
    }

    [Theory]
    [InlineData("newest", "newest", false)]
    [InlineData("random", "random", false)]
    [InlineData("RANDOM", "random", true)]
    [InlineData(" Newest ", "newest", true)]
    [InlineData("weekly", "newest", true)]
    [InlineData("", "newest", true)]
    [InlineData(null, "newest", true)]
    public void Sanitize_Mode_CaseInsensitiveCanonical_UnknownBecomesNewest(string? input, string expected, bool expectChanged)
    {
        var settings = new Settings { Mode = input! };

        bool changed = settings.Sanitize();

        Assert.Equal(expected, settings.Mode);
        Assert.Equal(expected == Settings.RandomMode, settings.IsRandomMode);
        Assert.Equal(expectChanged, changed);
    }

    [Fact]
    public void Sanitize_InvalidValues_AreLoggedWithTheOffendingValueEscaped()
    {
        var settings = new Settings { Market = "en-US&idx=7", Resolution = "4K\u0007", IntervalMinutes = 45, Mode = "weekly" };

        settings.Sanitize();

        Assert.Contains("settings invalid field=Market value='en-US&idx=7' using=en-US", LogText());
        Assert.Contains("settings invalid field=Resolution value='4K\\u0007' using=UHD", LogText());
        Assert.Contains("settings invalid field=IntervalMinutes value=45 using=30", LogText());
        Assert.Contains("settings invalid field=Mode value='weekly' using=newest", LogText());
    }

    [Fact]
    public void LoadOrCreate_Phase1File_WithoutIntervalOrMode_LoadsDefaults()
    {
        string path = Path.Combine(_dir, "settings.json");
        const string body = """{ "schemaVersion": 1, "market": "en-US", "resolution": "UHD" }""";
        File.WriteAllText(path, body);

        Settings settings = Settings.LoadOrCreate(path);

        Assert.Equal(30, settings.IntervalMinutes);
        Assert.Equal("newest", settings.Mode);
        Assert.False(settings.IsRandomMode);
        Assert.Equal(body, File.ReadAllText(path));   // a loaded file is never rewritten (T-01-08)
        Assert.DoesNotContain("settings invalid", LogText());
    }

    [Fact]
    public void Save_DoesNotWriteComputedProperties()
    {
        string path = Path.Combine(_dir, "settings.json");
        var settings = new Settings();

        settings.Save(path);
        string text = File.ReadAllText(path);

        Assert.Contains("\"intervalMinutes\": 30", text);
        Assert.Contains("\"mode\": \"newest\"", text);
        Assert.DoesNotContain("\"interval\"", text);
        Assert.DoesNotContain("isRandomMode", text);
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

    // ---- Phase 3 fields: Language, MonitorMode, Autostart, KnownMarkets ------------------------------------

    [Theory]
    [InlineData("perMonitor", "perMonitor", false)]
    [InlineData("same", "same", false)]
    [InlineData("PERMONITOR", "perMonitor", true)]
    [InlineData(" same ", "same", true)]
    [InlineData("dual", "same", true)]
    [InlineData("", "same", true)]
    [InlineData(null, "same", true)]
    public void Sanitize_MonitorMode_CanonicalisesOrFallsBack(string? input, string expected, bool expectChanged)
    {
        var settings = new Settings { MonitorMode = input! };

        bool changed = settings.Sanitize();

        Assert.Equal(expected, settings.MonitorMode);
        Assert.Equal(expected == Settings.PerMonitorMode, settings.IsPerMonitor);
        Assert.Equal(expectChanged, changed);
        if (input == "dual")
        {
            Assert.Contains("settings invalid field=MonitorMode value='dual' using=same", LogText());
        }
    }

    [Theory]
    [InlineData("vi", "vi", false)]
    [InlineData("en", "en", false)]
    [InlineData("auto", "auto", false)]
    [InlineData("VI", "vi", true)]
    [InlineData(" Auto ", "auto", true)]
    [InlineData("fr", "auto", true)]
    [InlineData("", "auto", true)]
    [InlineData(null, "auto", true)]
    public void Sanitize_Language_CanonicalisesOrFallsBack(string? input, string expected, bool expectChanged)
    {
        var settings = new Settings { Language = input! };

        bool changed = settings.Sanitize();

        Assert.Equal(expected, settings.Language);
        Assert.Equal(expectChanged, changed);
        if (input == "fr")
        {
            Assert.Contains("settings invalid field=Language value='fr' using=auto", LogText());
        }
    }

    [Fact]
    public void Defaults_NewFields_AreAutoSameTrue()
    {
        var settings = new Settings();

        Assert.Equal("auto", settings.Language);
        Assert.Equal("same", settings.MonitorMode);
        Assert.True(settings.Autostart);
        Assert.False(settings.IsPerMonitor);
    }

    [Fact]
    public void KnownMarkets_StartsWithEnUs_ContainsViVn_InFixedOrder()
    {
        Assert.Equal(
            ["en-US", "en-GB", "en-AU", "en-CA", "en-IN", "de-DE", "fr-FR", "ja-JP", "zh-CN", "vi-VN"],
            Settings.KnownMarkets);
        Assert.Equal(["same", "perMonitor"], Settings.KnownMonitorModes);
        Assert.Equal(["auto", "en", "vi"], Settings.KnownLanguages);
    }

    [Fact]
    public void LoadOrCreate_Phase2File_WithoutNewFields_LoadsDefaults_AndSanitizeReturnsFalse()
    {
        string path = Path.Combine(_dir, "settings.json");
        const string body = """{ "schemaVersion": 1, "market": "en-US", "resolution": "UHD", "intervalMinutes": 60, "mode": "random" }""";
        File.WriteAllText(path, body);

        Settings settings = Settings.LoadOrCreate(path);

        Assert.Equal("auto", settings.Language);
        Assert.Equal("same", settings.MonitorMode);
        Assert.True(settings.Autostart);
        Assert.False(settings.IsPerMonitor);
        Assert.Equal(60, settings.IntervalMinutes);
        Assert.Equal("random", settings.Mode);
        Assert.False(settings.Sanitize());   // missing new fields never force a rewrite (UI-03 empty edge)
        Assert.Equal(body, File.ReadAllText(path));
        Assert.DoesNotContain("settings invalid", LogText());
    }

    [Fact]
    public void Save_RoundTrips_NewFields()
    {
        string path = Path.Combine(_dir, "settings.json");
        var settings = new Settings { MonitorMode = "perMonitor", Language = "vi", Autostart = false };

        settings.Save(path);
        Settings loaded = Settings.LoadOrCreate(path);

        Assert.Equal("perMonitor", loaded.MonitorMode);
        Assert.True(loaded.IsPerMonitor);
        Assert.Equal("vi", loaded.Language);
        Assert.False(loaded.Autostart);
        string text = File.ReadAllText(path);
        Assert.Contains("\"monitorMode\": \"perMonitor\"", text);
        Assert.Contains("\"language\": \"vi\"", text);
        Assert.Contains("\"autostart\": false", text);
        Assert.DoesNotContain("isPerMonitor", text);
    }
}
