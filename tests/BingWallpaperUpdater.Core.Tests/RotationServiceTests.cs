using System.Text.RegularExpressions;
using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;
using BingWallpaperUpdater.Core.Ports;
using BingWallpaperUpdater.Core.Rotation;
using BingWallpaperUpdater.Core.Scheduling;
using Microsoft.Extensions.Time.Testing;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// End-to-end tick semantics through <see cref="RotationService"/> under a <see cref="FakeTimeProvider"/>: the
/// README fixture as the catalog (200 + ETag, then 304 on If-None-Match), HPImageArchive enrichment, a header-only
/// JPEG as every download, a counting applier and an inline UI dispatcher. Every clock advance is followed by
/// <see cref="RotationService.WaitForIdleAsync"/> because a tick may outlive the synchronous timer callback
/// (downloads use an asynchronous FileStream). Joins the "LogSink" collection: <c>tick ...</c> lines are asserted.
/// </summary>
[Collection("LogSink")]
public sealed class RotationServiceTests : IDisposable
{
    private const string GitHubHost = "raw.githubusercontent.com";
    private const string ArchivePath = "/HPImageArchive.aspx";
    private const string ImagePath = "/th";
    private const string Etag = "\"readme-v1\"";

    // README.sample.md rows (newest first) that also carry cached fixtures below.
    private const string NewestId = "OHR.AlphornBavaria_EN-US6200857270";   // 2026-09-20
    private const string MiddleId = "OHR.IcyCubs_EN-US5222104616";          // 2026-09-17
    private const string OldestId = "OHR.MisurinaPeak_EN-US4897144498";     // 2026-09-14

    // README.sample.md rows 2 and 3 (the second and third `download 4k` links): the first two backfill candidates (CACHE-06).
    private const string SecondId = "OHR.WinnatsPassPeak_EN-US6112068451";  // 2026-09-19
    private const string ThirdId = "OHR.Santenay_EN-US5299702509";          // 2026-09-18

    // Not in README.sample.md: the row MovedReadme() prepends when a test needs the catalog to move by a day.
    private const string FreshId = "OHR.ParisSunset_EN-US6532307523";       // 2026-09-21
    private const string MovedEtag = "\"readme-v2\"";

    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly string _dir;
    private readonly string _indexPath;
    private readonly string _statePath;
    private readonly string _catalogPath;
    private readonly string _logPath;
    private readonly FakeTimeProvider _time = new(T0);
    private readonly List<IDisposable> _owned = [];

    public RotationServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bwu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "index.json");
        _statePath = Path.Combine(_dir, "state.json");
        _catalogPath = Path.Combine(_dir, "catalog.md");
        _logPath = Path.Combine(_dir, "log.txt");
        Log.Initialize(_logPath);
    }

    public void Dispose()
    {
        foreach (IDisposable d in _owned)
        {
            d.Dispose();
        }

        Log.Initialize(Path.Combine(_dir, "closed.log"));
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- fixture helpers -----------------------------------------------------------------------------

    private string LogText() => File.Exists(_logPath) ? File.ReadAllText(_logPath) : string.Empty;

    private int LogCount(string pattern) => Regex.Matches(LogText(), Regex.Escape(pattern)).Count;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static RetryPolicy NoDelay() => new(delay: (_, _) => Task.CompletedTask, random: new Random(1));

    /// <summary>GitHub README 200 + ETag, 304 once the request carries If-None-Match; HPImageArchive; JPEG downloads on both Bing hosts.</summary>
    private static FakeHttpHandler Routes(string? readme = null)
    {
        string body = readme ?? Fixture("README.sample.md");
        string archive = Fixture("hpimagearchive.sample.json");
        var fake = new FakeHttpHandler();
        fake.Map(GitHubHost, "/", req => req.Headers.IfNoneMatch.Count > 0
            ? FakeHttpHandler.Text(304, string.Empty, Etag)
            : FakeHttpHandler.Text(200, body, Etag));
        foreach (string host in new[] { BingImageUrl.PrimaryHost, BingImageUrl.RetryHost })
        {
            fake.Map(host, ArchivePath, _ => FakeHttpHandler.Json(200, archive));
            fake.Map(host, ImagePath, _ => FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(3840, 2160)));
        }

        return fake;
    }

    /// <summary>
    /// The same routes as <see cref="Routes(string?)"/>, except that a download whose query names <paramref name="deadId"/>
    /// answers the CDN's 404-as-jpeg placeholder (status 404, <c>image/jpeg</c>, a bare FF D8 body) on both Bing hosts;
    /// every other image request falls through to the normal JPEG. One catalog ID that can never be cached (CACHE-06).
    /// </summary>
    private static FakeHttpHandler RoutesWithDeadImage(string deadId)
    {
        string body = Fixture("README.sample.md");
        string archive = Fixture("hpimagearchive.sample.json");
        var fake = new FakeHttpHandler();
        fake.Map(GitHubHost, "/", req => req.Headers.IfNoneMatch.Count > 0
            ? FakeHttpHandler.Text(304, string.Empty, Etag)
            : FakeHttpHandler.Text(200, body, Etag));
        foreach (string host in new[] { BingImageUrl.PrimaryHost, BingImageUrl.RetryHost })
        {
            fake.Map(host, ArchivePath, _ => FakeHttpHandler.Json(200, archive));
            fake.Map(host, ImagePath, req => req.RequestUri!.Query.Contains(deadId, StringComparison.Ordinal)
                ? FakeHttpHandler.Bytes(404, "image/jpeg", new byte[] { 0xFF, 0xD8 })
                : FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(3840, 2160)));
        }

        return fake;
    }

    /// <summary>
    /// Mutable catalog sources for <see cref="Routes(Catalog)"/>: swap <see cref="Body"/> mid-test to make the README
    /// move, flip <see cref="GitHubOnline"/> to take GitHub away, swap <see cref="Archive"/> to move HPImageArchive.
    /// </summary>
    private sealed class Catalog
    {
        public bool GitHubOnline { get; set; } = true;
        public string Body { get; set; } = Fixture("README.sample.md");
        public string Archive { get; set; } = Fixture("hpimagearchive.sample.json");
    }

    /// <summary>
    /// The same routes as <see cref="Routes(string?)"/>, but both catalog bodies are read from <paramref name="catalog"/>
    /// on every request. The README carries an ETag per body version, so a swapped body answers 200 with the new ETag
    /// while an unchanged one keeps answering 304 to If-None-Match; GitHub throws like <see cref="Offline"/> while
    /// <see cref="Catalog.GitHubOnline"/> is false.
    /// </summary>
    private static FakeHttpHandler Routes(Catalog catalog)
    {
        string original = Fixture("README.sample.md");
        var fake = new FakeHttpHandler();
        fake.Map(GitHubHost, "/", req =>
        {
            if (!catalog.GitHubOnline)
            {
                throw new HttpRequestException("simulated github outage");
            }

            string body = catalog.Body;
            string etag = string.Equals(body, original, StringComparison.Ordinal) ? Etag : MovedEtag;
            return req.Headers.IfNoneMatch.Any(t => string.Equals(t.Tag, etag, StringComparison.Ordinal))
                ? FakeHttpHandler.Text(304, string.Empty, etag)
                : FakeHttpHandler.Text(200, body, etag);
        });
        foreach (string host in new[] { BingImageUrl.PrimaryHost, BingImageUrl.RetryHost })
        {
            fake.Map(host, ArchivePath, _ => FakeHttpHandler.Json(200, catalog.Archive));
            fake.Map(host, ImagePath, _ => FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(3840, 2160)));
        }

        return fake;
    }

    /// <summary>hpimagearchive.sample.json with its newest entry renamed to <see cref="FreshId"/>: HPImageArchive rolled over a day before the README did.</summary>
    private static string ArchiveAhead() => Fixture("hpimagearchive.sample.json").Replace(NewestId, FreshId, StringComparison.Ordinal);

    /// <summary>README.sample.md with one row (<see cref="FreshId"/>, 2026-09-21) inserted ahead of every existing row: the catalog moved by a day.</summary>
    private static string MovedReadme()
    {
        string body = Fixture("README.sample.md");
        const string headerRow = "| :----: | :----: | :----: |";
        int at = body.IndexOf(headerRow, StringComparison.Ordinal);
        Assert.True(at >= 0, "README.sample.md table header not found");
        int lineEnd = body.IndexOf('\n', at) + 1;
        string row = $"|![](https://cn.bing.com/th?id={FreshId}_UHD.jpg&pid=hp&w=384&h=216&rs=1&c=4)2026-09-21 [download 4k](https://cn.bing.com/th?id={FreshId}_UHD.jpg&rf=LaDigue_UHD.jpg&pid=hp&w=3840&h=2160&rs=1&c=4)|\n";
        return body.Insert(lineEnd, row);
    }

    private static readonly Regex ReadmeRow = new(@"(?<date>\d{4}-\d{2}-\d{2}) \[download 4k\]\(https://cn\.bing\.com/th\?id=(?<id>OHR\.[A-Za-z0-9]+_[A-Z]{2}-[A-Z]{2}\d+)_UHD\.jpg", RegexOptions.CultureInvariant);

    /// <summary>The (date, id) pairs of README.sample.md's `download 4k` links, newest first.</summary>
    private static IReadOnlyList<(string Date, string Id)> ReadmeRows() =>
        ReadmeRow.Matches(Fixture("README.sample.md")).Select(m => (m.Groups["date"].Value, m.Groups["id"].Value)).ToList();

    /// <summary>
    /// README.sample.md with its table replaced by one row per kept (date, id) pair, in fixture order; the header
    /// lines (title, hero line, table header) are kept verbatim so the parser still sees the table.
    /// </summary>
    private static string ReadmeKeeping(Func<(string Date, string Id), int, bool> keep)
    {
        string body = Fixture("README.sample.md");
        const string headerRow = "| :----: | :----: | :----: |";
        int at = body.IndexOf(headerRow, StringComparison.Ordinal);
        Assert.True(at >= 0, "README.sample.md table header not found");
        int lineEnd = body.IndexOf('\n', at) + 1;
        var sb = new System.Text.StringBuilder(body[..lineEnd]);
        foreach (((string date, string id), int index) in ReadmeRows().Select((row, i) => (row, i)))
        {
            if (keep((date, id), index))
            {
                sb.Append($"|![](https://cn.bing.com/th?id={id}_UHD.jpg&pid=hp&w=384&h=216&rs=1&c=4){date} [download 4k](https://cn.bing.com/th?id={id}_UHD.jpg&rf=LaDigue_UHD.jpg&pid=hp&w=3840&h=2160&rs=1&c=4)|\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>README.sample.md cut after its Nth `download 4k` row (CACHE-06 no-candidate facts).</summary>
    private static string ReadmeWithRows(int count) => ReadmeKeeping((_, i) => i < count);

    /// <summary>
    /// README.sample.md reduced to the listed IDs (fixture order), so the catalog holds exactly those IDs and a
    /// successful tick has nothing to backfill (CACHE-06: `backfill skipped cause=no-candidate`). The scenario facts
    /// that seed a small cache and reason about which cached image Next / Random / per-monitor picks use this to keep
    /// the cache they describe: against the full 30-row catalog every successful tick would add one more day.
    /// </summary>
    private static string ReadmeOf(params string[] ids) => ReadmeKeeping((row, _) => ids.Contains(row.Id, StringComparer.Ordinal));

    /// <summary><see cref="Routes(string?)"/> over <see cref="ReadmeOf"/>: the catalog pinned to the given IDs.</summary>
    private static FakeHttpHandler RoutesFor(params string[] ids) => Routes(ReadmeOf(ids));

    /// <summary>Every host unreachable: the single responder throws <see cref="HttpRequestException"/> for every request (both catalog sources and both image hosts).</summary>
    private static FakeHttpHandler Offline() => new(FakeHttpHandler.Throw(new HttpRequestException("simulated offline")));

    /// <summary>Mutable network state for <see cref="Switchable"/>: flip <see cref="Online"/> mid-test to simulate the network returning.</summary>
    private sealed class Network
    {
        public bool Online { get; set; }
    }

    /// <summary>Offline while <c>network.Online</c> is false (throws like <see cref="Offline"/>); the same routes as <see cref="Routes"/> once it is true.</summary>
    private static FakeHttpHandler Switchable(Network network)
    {
        string body = Fixture("README.sample.md");
        string archive = Fixture("hpimagearchive.sample.json");
        return new FakeHttpHandler(req =>
        {
            if (!network.Online)
            {
                throw new HttpRequestException("simulated offline");
            }

            Uri uri = req.RequestUri!;
            if (string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase))
            {
                return req.Headers.IfNoneMatch.Count > 0
                    ? FakeHttpHandler.Text(304, string.Empty, Etag)
                    : FakeHttpHandler.Text(200, body, Etag);
            }

            if (uri.AbsolutePath.StartsWith(ArchivePath, StringComparison.Ordinal))
            {
                return FakeHttpHandler.Json(200, archive);
            }

            if (uri.AbsolutePath.StartsWith(ImagePath, StringComparison.Ordinal))
            {
                return FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(3840, 2160));
            }

            throw new UnroutedRequestException($"no fake route for {req.Method} {uri}");
        });
    }

    /// <summary>Seeded entries were on disk before the app started at <see cref="T0"/>: they are stamped the day before.</summary>
    private static readonly DateTimeOffset SeedT0 = T0.AddDays(-1);

    /// <summary>
    /// A pre-existing index entry. It carries no <see cref="CachedImage.Seq"/> (an index written by an earlier build),
    /// so the decider's <c>DownloadedUtc</c> fallback is what orders it against images downloaded during the test —
    /// which is why the stamp must precede <see cref="T0"/>.
    /// </summary>
    private static CachedImage Cached(string id, string date, int downloadedMinutesAfterSeedT0 = 0, string resolution = "UHD", int width = 3840) => new()
    {
        Id = id,
        Market = "EN-US",
        Date = date,
        Resolution = resolution,
        Width = width,
        Height = width * 9 / 16,
        File = $"{date}_{id}.jpg",
        Bytes = 16,
        DownloadedUtc = SeedT0.AddMinutes(downloadedMinutesAfterSeedT0),
    };

    /// <summary>Writes index.json and a 16-byte file per image (unless listed in <paramref name="missingFiles"/>), then loads the cache.</summary>
    private ImageCache SeedCache(IReadOnlyList<CachedImage> images, string? applied = null, params string[] missingFiles)
    {
        var index = new CacheIndex
        {
            Applied = applied is null ? [] : [applied],
            Images = [.. images],
        };
        AtomicJsonFile.Save(_indexPath, index, CoreJsonContext.Default.CacheIndex);
        foreach (CachedImage image in images)
        {
            if (!missingFiles.Contains(image.Id, StringComparer.Ordinal))
            {
                File.WriteAllBytes(Path.Combine(_dir, image.File), new byte[16]);
            }
        }

        var cache = new ImageCache(_dir, _indexPath, _time);   // WR-01: DownloadedUtc comes from the same fake clock as the scheduler
        cache.Load();
        return cache;
    }

    private static IReadOnlyList<CachedImage> ThreeCached() =>
    [
        Cached(NewestId, "2026-09-20", 120),
        Cached(MiddleId, "2026-09-17", 60),
        Cached(OldestId, "2026-09-14", 0),
    ];

    private sealed record Harness(RotationService Service, AppState State, FakeApplier Applier, FakeHttpHandler Http, ImageCache Cache);

    private Harness Build(Settings settings, AppState state, ImageCache? cache = null, FakeHttpHandler? fake = null, FakeApplier? applier = null, Core.Ports.IUiDispatcher? dispatcher = null, Core.Ports.IMonitorLayout? monitors = null)
    {
        fake ??= Routes();
        applier ??= new FakeApplier();
        cache ??= SeedCache([]);
        var http = new HttpGateway(fake, retry: NoDelay()) { DownloadRoot = _dir };
        _owned.Add(http);
        var catalog = new CatalogService(http, state, _catalogPath);
        var service = new RotationService(settings, state, _statePath, catalog, cache, http, applier, dispatcher ?? new InlineUiDispatcher(), monitors ?? new FakeMonitorLayout([(1920, 1080)]), _time, new Random(20260920));
        _owned.Add(service);
        return new Harness(service, state, applier, fake, cache);
    }

    private static Settings Newest() => new() { IntervalMinutes = 30, Mode = Settings.DefaultMode };

    private static Settings RandomMode() => new() { IntervalMinutes = 30, Mode = Settings.RandomMode };

    private static Settings WithMode(string mode) => new() { IntervalMinutes = 30, Mode = mode };

    private AppState? SavedState() => AtomicJsonFile.Load(_statePath, CoreJsonContext.Default.AppState);

    private string SavedStateText() => File.Exists(_statePath) ? File.ReadAllText(_statePath) : string.Empty;

    /// <summary>The retry ladder is in memory only (D-13): the persisted file must never mention it.</summary>
    private void AssertStateFileHasNoRetry() =>
        Assert.DoesNotContain("retry", SavedStateText(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Start the heartbeat with no initial delay and wait for the startup tick to finish. A zero due time fires inside
    /// <c>CreateTimer</c> on <see cref="FakeTimeProvider"/>; the zero-length advance only guards the other case, so the
    /// startup tick always sees exactly the current fake time.
    /// </summary>
    private async Task StartAsync(Harness h)
    {
        h.Service.Start(TimeSpan.Zero, CancellationToken.None);
        _time.Advance(TimeSpan.Zero);
        await h.Service.WaitForIdleAsync();
    }

    private async Task AdvanceAsync(Harness h, TimeSpan by)
    {
        _time.Advance(by);
        await h.Service.WaitForIdleAsync();
    }

    // ---- Task 1: the tracer path ---------------------------------------------------------------------

    [Fact]
    public async Task StartupTick_NewCatalogId_AppliesWithinOneTick_SetsLastSeenAndNextDue()
    {
        Harness h = Build(Newest(), new AppState());

        await StartAsync(h);

        Assert.Equal(1, h.Applier.Calls);
        Assert.EndsWith(".jpg", h.Applier.LastPath!, StringComparison.Ordinal);
        Assert.True(File.Exists(h.Applier.LastPath));
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.Equal(NewestId, h.State.LastSeenNewestId);
        Assert.Equal(T0 + Interval, h.State.NextDueUtc);
        Assert.Equal(T0, h.State.LastCheckUtc);
        Assert.Equal(T0, h.State.LastAppliedUtc);

        AppState? saved = SavedState();
        Assert.NotNull(saved);
        Assert.Equal(NewestId, saved!.LastSeenNewestId);
        Assert.Equal(T0 + Interval, saved.NextDueUtc);

        string log = LogText();
        Assert.Contains("tick reason=Startup mode=newest interval=30", log);
        Assert.Contains("tick done reason=Startup result=Applied decision=ApplyNew why=new", log);
        Assert.Contains($"next={(T0 + Interval):O}", log);
    }

    [Fact]
    public async Task Interval_UnchangedCatalog_NoOp_ZeroApplies_304Reused()
    {
        Harness h = Build(Newest(), new AppState());
        await StartAsync(h);
        h.Applier.Reset();

        await AdvanceAsync(h, Interval);

        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Contains("tick done reason=Interval result=NoOp decision=NoOp why=unchanged", LogText());

        List<RecordedRequest> readme = h.Http.RequestsTo(GitHubHost).ToList();
        Assert.Equal(2, readme.Count);
        Assert.Null(readme[0].Header("If-None-Match"));
        Assert.Equal(Etag, readme[1].Header("If-None-Match"));

        DateTimeOffset now = _time.GetUtcNow();
        Assert.Equal(now, h.State.LastCheckUtc);
        Assert.Equal(now + Interval, h.State.NextDueUtc);
        AppState? saved = SavedState();
        Assert.NotNull(saved);
        Assert.Equal(now, saved!.LastCheckUtc);
        Assert.Equal(NewestId, saved.LastSeenNewestId);
    }

    [Fact]
    public async Task SleepGap_EightHours_ExactlyOneCatchUpTick()
    {
        // Random mode with two cached images so the single catch-up tick is visible as exactly one apply (D-04, D-06).
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20", 60), Cached(MiddleId, "2026-09-17", 0)], applied: NewestId);
        var state = new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0.AddMinutes(20) };
        Harness h = Build(RandomMode(), state, cache, RoutesFor(NewestId, MiddleId));   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);
        Assert.Equal(0, h.Applier.Calls);   // not due yet: the launch leaves the desktop alone (D-11)
        Assert.Equal(T0.AddMinutes(20), h.State.NextDueUtc);

        await AdvanceAsync(h, TimeSpan.FromHours(8));   // 480 heartbeat callbacks, all seeing the end time

        DateTimeOffset end = _time.GetUtcNow();
        Assert.Equal(T0.AddHours(8), end);
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(1, h.Applier.Calls);
        Assert.Contains(MiddleId, h.Applier.LastPath!);
        Assert.Equal(end + Interval, h.State.NextDueUtc);   // now + interval, never nextDue + n * interval
        Assert.Equal(0, LogCount("cause=busy"));            // heartbeats during an in-flight tick are silent
    }

    // ---- Task 2: scheduler edges through the service --------------------------------------------------

    /// <summary>Persist a state, then reload it from disk the way a restarted process would.</summary>
    private AppState Persisted(AppState state)
    {
        state.Save(_statePath);
        return AppState.LoadOrCreate(_statePath);
    }

    [Fact]
    public async Task Restart_NotDue_KeepsPersistedNextDue()
    {
        DateTimeOffset persistedDue = T0.AddMinutes(20);
        AppState state = Persisted(new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = persistedDue });
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        _time.Advance(TimeSpan.FromMinutes(5));   // the app restarts 5 min later, 15 min before the schedule is due
        Harness h = Build(Newest(), state, cache);

        await StartAsync(h);

        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(persistedDue, h.State.NextDueUtc);   // the countdown is NOT restarted from zero (SC2, Pitfall 4)
        Assert.Equal(persistedDue, SavedState()!.NextDueUtc);
        Assert.Contains("tick done reason=Startup result=NoOp decision=NoOp why=not-due", LogText());
        Assert.Equal(_time.GetUtcNow(), h.State.LastCheckUtc);   // but the catalog was still checked (D-11)
    }

    [Fact]
    public async Task Restart_PastDue_OneTickAndRearm()
    {
        AppState state = Persisted(new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0.AddHours(-3) });
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(Newest(), state, cache);

        await StartAsync(h);

        Assert.Equal(1, LogCount("tick reason=Startup"));
        Assert.Equal(0, h.Applier.Calls);   // unchanged catalog: catch-up re-arms without touching the desktop
        Assert.Equal(_time.GetUtcNow() + Interval, h.State.NextDueUtc);
        Assert.Contains("tick done reason=Startup result=NoOp decision=NoOp why=unchanged", LogText());

        await AdvanceAsync(h, TimeSpan.FromMinutes(5));   // still not due: no second tick
        Assert.Equal(0, LogCount("tick reason=Interval"));
    }

    [Fact]
    public async Task Upgrade_FromPhase1State_SeedsLastSeen_NoReapply()
    {
        const string phase1 = """
            {
              "schemaVersion": 1,
              "catalogEtag": "W/stale",
              "catalogFetchedUtc": "2026-09-20T02:51:00.0000000+00:00",
              "currentImageId": "OHR.AlphornBavaria_EN-US6200857270",
              "lastAppliedUtc": "2026-09-20T02:51:01.0000000+00:00"
            }
            """;
        File.WriteAllText(_statePath, phase1);
        AppState state = AppState.LoadOrCreate(_statePath);
        Assert.Null(state.LastSeenNewestId);
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20")], applied: NewestId);
        Harness h = Build(Newest(), state, cache);

        await StartAsync(h);

        Assert.Equal(0, h.Applier.Calls);   // the image already on the desktop is not re-applied (Pitfall 3)
        Assert.Equal(NewestId, h.State.LastSeenNewestId);
        Assert.Equal(NewestId, SavedState()!.LastSeenNewestId);
        Assert.Equal(T0 + Interval, h.State.NextDueUtc);   // first Phase 2 launch arms the schedule
        Assert.Contains("tick done reason=Startup result=NoOp decision=SeedLastSeen why=seed", LogText());
    }

    [Fact]
    public async Task Tick_MissingCurrentFile_AppliesNewest()
    {
        var state = new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId };
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20")], applied: NewestId, missingFiles: NewestId);
        Harness h = Build(Newest(), state, cache);

        await StartAsync(h);

        Assert.Equal(1, h.Applier.Calls);
        Assert.True(File.Exists(h.Applier.LastPath));
        Assert.Contains("AlphornBavaria_EN-US6200857270", h.Applier.LastPath!);   // CacheFileName drops the OHR. prefix
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.Contains("tick done reason=Startup result=Applied decision=ApplyNew why=missing-current", LogText());
        Assert.Contains("cache add id=" + NewestId, LogText());   // re-downloaded, not served from the dead index entry
    }

    // ---- Task 2: Next and random mode through the service --------------------------------------------

    [Fact]
    public async Task Next_Newest_StepsOlderAndWraps_RearmsNextDue()
    {
        var state = new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0.AddMinutes(20) };
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(Newest(), state, cache, RoutesFor(NewestId, MiddleId, OldestId));   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);
        Assert.Equal(0, h.Applier.Calls);

        foreach (string expected in new[] { MiddleId, OldestId, NewestId })
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            TickResult result = await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None);

            Assert.Equal(TickResult.Applied, result);
            Assert.Contains(expected, h.Applier.LastPath!);
            Assert.Equal(expected, h.State.CurrentImageId);
            Assert.Equal(_time.GetUtcNow() + Interval, h.State.NextDueUtc);   // a successful Next re-arms (D-05)
        }

        Assert.Equal(3, h.Applier.Calls);
        Assert.Equal(NewestId, h.State.LastSeenNewestId);   // stepping back never rewrites what was "seen" (D-03)
        Assert.Equal(3, LogCount("tick done reason=Next result=Applied decision=StepOlder why=next"));
    }

    [Fact]
    public async Task Next_Random_NeverCurrent_20Calls()
    {
        var state = new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0.AddMinutes(20) };
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(RandomMode(), state, cache);
        await StartAsync(h);

        for (int i = 0; i < 20; i++)
        {
            string before = h.State.CurrentImageId!;
            TickResult result = await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None);

            Assert.Equal(TickResult.Applied, result);
            Assert.NotEqual(before, h.State.CurrentImageId);
            Assert.DoesNotContain(before, h.Applier.LastPath!);
        }

        Assert.Equal(20, h.Applier.Calls);
        Assert.Equal(20, LogCount("decision=Random why=next"));
    }

    [Fact]
    public async Task Next_SingleImage_LogsOnlyNoApply()
    {
        DateTimeOffset persistedDue = T0.AddMinutes(20);
        var state = new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = persistedDue };
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20")], applied: NewestId);
        Harness h = Build(Newest(), state, cache, RoutesFor(NewestId));   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);

        TickResult result = await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None);

        Assert.Equal(TickResult.NoOp, result);
        Assert.Equal(0, h.Applier.Calls);
        Assert.Contains("tick done reason=Next result=NoOp decision=NoOp why=single-image", LogText());
        Assert.Equal(persistedDue, h.State.NextDueUtc);   // nothing applied, nothing re-armed (D-05)
    }

    [Fact]
    public async Task Next_WhileTickRunning_ReturnsBusy_AndLogs()
    {
        var dispatcher = new BlockingUiDispatcher();
        Harness h = Build(Newest(), new AppState(), dispatcher: dispatcher);

        Task<TickResult> first = h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(h.Service.IsTickRunning);

        TickResult second = await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None);

        Assert.Equal(TickResult.Busy, second);
        Assert.Contains("tick skipped reason=Next cause=busy", LogText());
        Assert.Equal(0, h.Applier.Calls);   // the first tick is still parked at its apply

        dispatcher.Release();
        Assert.Equal(TickResult.Applied, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(h.Service.IsTickRunning);
        Assert.Equal(1, h.Applier.Calls);
    }

    [Fact]
    public async Task Interval_Random_TwoCached_RotatesToOther()
    {
        var state = new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0 + Interval };
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20", 60), Cached(MiddleId, "2026-09-17", 0)], applied: NewestId);
        Harness h = Build(RandomMode(), state, cache, RoutesFor(NewestId, MiddleId));   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);
        Assert.Equal(0, h.Applier.Calls);

        await AdvanceAsync(h, Interval);

        Assert.Equal(1, h.Applier.Calls);
        Assert.Contains(MiddleId, h.Applier.LastPath!);
        Assert.Equal(MiddleId, h.State.CurrentImageId);
        Assert.Contains("tick done reason=Interval result=Applied decision=Random why=interval", LogText());
    }

    [Fact]
    public async Task ScheduledTick_AfterNextStepBack_DoesNotUndoUserChoice()
    {
        var state = new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0.AddMinutes(20) };
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(Newest(), state, cache, RoutesFor(NewestId, MiddleId, OldestId));   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);

        Assert.Equal(TickResult.Applied, await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None));
        Assert.Equal(MiddleId, h.State.CurrentImageId);
        h.Applier.Reset();

        await AdvanceAsync(h, Interval);   // the scheduled tick after the step-back

        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(0, h.Applier.Calls);                     // the unchanged catalog never re-applies the newest (D-03)
        Assert.Equal(MiddleId, h.State.CurrentImageId);
        Assert.Equal(NewestId, h.State.LastSeenNewestId);
        Assert.Contains("tick done reason=Interval result=NoOp decision=NoOp why=unchanged", LogText());
    }

    [Fact]
    public async Task SourceSwitch_ArchiveAhead_GitHubRecovers_NeverRegressesOrFlipFlops()
    {
        // WR-01: the README and HPImageArchive do not roll over at the same instant. The archive already lists
        // FreshId while the README still lists NewestId; a GitHub outage applies FreshId once, GitHub recovering must
        // NOT re-apply the older NewestId, and the README catching up must NOT apply FreshId a second time.
        var catalog = new Catalog();
        Harness h = Build(Newest(), new AppState(), fake: Routes(catalog));
        await StartAsync(h);                                 // README newest applied at T0
        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(NewestId, h.State.LastSeenNewestId, StringComparer.Ordinal);

        catalog.GitHubOnline = false;
        catalog.Archive = ArchiveAhead();
        await AdvanceAsync(h, Interval);                     // T0 + 30: HPImageArchive is the catalog, its newest is genuinely unseen

        Assert.Equal(2, h.Applier.Calls);
        Assert.Contains("ParisSunset", h.Applier.LastPath!, StringComparison.Ordinal);
        Assert.Equal(FreshId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(FreshId, h.State.LastSeenNewestId, StringComparer.Ordinal);
        Assert.Contains("catalog source=hpimagearchive", LogText());
        Assert.Contains("tick done reason=Interval result=Applied decision=ApplyNew why=new", LogText());
        Assert.Equal("2026-09-20", h.Cache.Index.Images.Single(i => i.Id == FreshId).Date);   // IN-11: archive rows carry Bing's enddate

        catalog.GitHubOnline = true;                         // README still on yesterday's NewestId
        await AdvanceAsync(h, Interval);                     // T0 + 60

        Assert.Equal(2, h.Applier.Calls);                    // no regression to the older image
        Assert.Equal(FreshId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(FreshId, h.State.LastSeenNewestId, StringComparer.Ordinal);
        Assert.Equal(2, LogCount("tick reason=Interval"));
        Assert.Equal(1, LogCount("tick done reason=Interval result=NoOp decision=NoOp why=unchanged"));

        catalog.Body = MovedReadme();                        // the README catches up to FreshId
        await AdvanceAsync(h, Interval);                     // T0 + 90

        Assert.Equal(2, h.Applier.Calls);                    // no second apply of FreshId
        Assert.Equal(3, LogCount("tick reason=Interval"));
        Assert.Equal(2, LogCount("tick done reason=Interval result=NoOp decision=NoOp why=unchanged"));
        Assert.Equal(1, LogCount("cache add id=" + FreshId));
        Assert.Equal(FreshId, SavedState()!.LastSeenNewestId);
    }

    [Fact]
    public async Task SourceSwitch_ArchiveAhead_BackwardClockBetweenDownloads_NeverRegressesOrFlipFlops()
    {
        // WR-01 (iteration 2): the same source switch, but the clock is corrected back 3 h between the two
        // downloads, so the genuinely newer FreshId carries an EARLIER DownloadedUtc than NewestId. Cache order
        // (Seq) must decide "already known", not the wall clock (D-07).
        var catalog = new Catalog();
        Harness h = Build(Newest(), new AppState(), fake: Routes(catalog));
        await StartAsync(h);                                 // README newest applied at T0
        Assert.Equal(1, h.Applier.Calls);
        DateTimeOffset newestStamp = h.Cache.Index.Images.Single(i => i.Id == NewestId).DownloadedUtc;
        Assert.Equal(T0, newestStamp);

        _time.AdjustTime(_time.GetUtcNow() - TimeSpan.FromHours(3));
        h.Service.OnClockChanged();                          // NextDueUtc clamped to now + Interval

        catalog.GitHubOnline = false;
        catalog.Archive = ArchiveAhead();
        await AdvanceAsync(h, Interval);                     // T0 - 2h30: HPImageArchive is the catalog, FreshId applied

        Assert.Equal(2, h.Applier.Calls);
        Assert.Equal(FreshId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(FreshId, h.State.LastSeenNewestId, StringComparer.Ordinal);
        CachedImage fresh = h.Cache.Index.Images.Single(i => i.Id == FreshId);
        Assert.True(fresh.DownloadedUtc < newestStamp);      // the inverted stamp the old rule tripped on
        Assert.True(fresh.Seq > h.Cache.Index.Images.Single(i => i.Id == NewestId).Seq);

        catalog.GitHubOnline = true;                         // README still on yesterday's NewestId
        await AdvanceAsync(h, Interval);

        Assert.Equal(2, h.Applier.Calls);                    // no regression to the older image
        Assert.Equal(FreshId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(FreshId, h.State.LastSeenNewestId, StringComparer.Ordinal);

        catalog.Body = MovedReadme();                        // the README catches up to FreshId
        await AdvanceAsync(h, Interval);

        Assert.Equal(2, h.Applier.Calls);                    // no second apply of FreshId
        Assert.Equal(1, LogCount("cache add id=" + FreshId));
        Assert.Equal(FreshId, SavedState()!.LastSeenNewestId);
    }

    [Fact]
    public async Task Dispose_StopsHeartbeat_LaterTicksAreCancelled()
    {
        Harness h = Build(Newest(), new AppState());
        await StartAsync(h);
        h.Applier.Reset();

        h.Service.Dispose();
        await AdvanceAsync(h, TimeSpan.FromHours(2));

        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(0, LogCount("tick reason=Interval"));
        Assert.Equal(TickResult.Cancelled, await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None));
    }

    // ---- Plan 02-02 Task 1: retry ladder and the offline matrix (D-13, D-14, ROT-06, CACHE-04) --------

    /// <summary>Current image applied and present, catalog already seen, schedule persisted for <paramref name="dueInMinutes"/> from T0.</summary>
    private static AppState SeenState(string current = NewestId, int? dueInMinutes = 30) => new()
    {
        CurrentImageId = current,
        LastSeenNewestId = current,
        NextDueUtc = dueInMinutes is { } m ? T0.AddMinutes(m) : null,
    };

    [Fact]
    public async Task Backoff_5_15_ThenInterval()
    {
        // Newest mode, one cached image already on the desktop, nothing persisted yet: the startup tick is due and
        // re-arms the schedule to T0 + 30 min, but its fetch fails on every host.
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20")], applied: NewestId);
        Harness h = Build(Newest(), SeenState(dueInMinutes: null), cache, Offline());

        await StartAsync(h);

        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(1, h.Service.FailureStage);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        Assert.Contains("pipeline failed stage=catalog error=no catalog source produced rows", LogText());   // the tick owns this token
        Assert.Contains("tick done reason=Startup result=FetchFailed decision=NoOp why=unchanged", LogText());
        Assert.Contains($"retry scheduled stage=1 at={(T0 + TimeSpan.FromMinutes(5)):O}", LogText());
        Assert.Contains($"retry={(T0 + TimeSpan.FromMinutes(5)):O}", LogText());
        AssertStateFileHasNoRetry();

        await AdvanceAsync(h, TimeSpan.FromMinutes(5));   // first retry, about 5 min after the failure

        DateTimeOffset now = _time.GetUtcNow();
        Assert.Equal(1, LogCount("tick reason=Retry"));
        Assert.Equal(2, h.Service.FailureStage);
        Assert.Equal(now + TimeSpan.FromMinutes(15), h.Service.RetryDueUtc);
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);   // a Retry tick never touches the schedule
        Assert.Contains($"retry scheduled stage=2 at={(now + TimeSpan.FromMinutes(15)):O}", LogText());
        AssertStateFileHasNoRetry();

        await AdvanceAsync(h, TimeSpan.FromMinutes(15));  // second retry, about 15 min later

        Assert.Equal(2, LogCount("tick reason=Retry"));
        Assert.Equal(3, h.Service.FailureStage);
        Assert.Null(h.Service.RetryDueUtc);
        Assert.Contains($"retry exhausted stage=3 next={(T0 + Interval):O}", LogText());
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        AssertStateFileHasNoRetry();

        await AdvanceAsync(h, TimeSpan.FromMinutes(10));  // the 30 min mark: no retry pending, the interval-due tick attempts the fetch

        Assert.Equal(_time.GetUtcNow(), T0 + Interval);
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(2, LogCount("tick reason=Retry"));
        Assert.Equal(4, h.Service.FailureStage);
        Assert.Null(h.Service.RetryDueUtc);                  // beyond the ladder nothing shrinks the wait back (no loop)
        Assert.Equal(_time.GetUtcNow() + Interval, h.Service.NextDueUtc);
        Assert.Equal(0, h.Applier.Calls);                    // newest mode offline never touches the desktop (D-14)
        AssertStateFileHasNoRetry();
    }

    [Theory]
    [InlineData(Settings.DefaultMode)]
    [InlineData(Settings.RandomMode)]
    public async Task RetryTick_FetchStillFailing_NeverRotates_BothModes(string mode)
    {
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(WithMode(mode), SeenState(), cache, Offline());
        await StartAsync(h);   // not due: NoOp(not-due) + FetchFailed, retry armed at T0 + 5 min
        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);

        await AdvanceAsync(h, TimeSpan.FromMinutes(5));
        await AdvanceAsync(h, TimeSpan.FromMinutes(15));

        Assert.Equal(2, LogCount("tick reason=Retry"));
        Assert.Equal(2, LogCount("tick done reason=Retry result=FetchFailed decision=NoOp why=retry"));
        Assert.Equal(0, h.Applier.Calls);                          // a retry-only tick never changes the desktop (D-13)
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.Equal(0, LogCount("tick reason=Interval"));         // still 10 min short of the schedule
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
    }

    [Fact]
    public async Task Offline_Random_IntervalTick_RotatesToOtherCachedImage()
    {
        // The persisted schedule falls due 2 min after launch (a restart late in the interval), well before the ladder
        // would be exhausted, so the interval-due tick both fails its fetch and rotates from the cache (D-14).
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(RandomMode(), SeenState(dueInMinutes: 2), cache, Offline());
        await StartAsync(h);
        Assert.Equal(0, h.Applier.Calls);

        await AdvanceAsync(h, TimeSpan.FromMinutes(2));

        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(1, h.Applier.Calls);
        Assert.False(string.Equals(NewestId, h.State.CurrentImageId, StringComparison.Ordinal));
        Assert.DoesNotContain(NewestId, h.Applier.LastPath!);
        Assert.Contains(h.State.CurrentImageId!.Substring(4), h.Applier.LastPath!);   // CacheFileName drops the OHR. prefix
        Assert.Equal(NewestId, h.State.LastSeenNewestId);                             // rotating the cache never marks anything seen (D-03)

        string doneLine = LogText().Split('\n').Single(l => l.Contains("tick done reason=Interval", StringComparison.Ordinal));
        Assert.Contains("result=Applied decision=Random why=interval", doneLine);
        Assert.DoesNotContain("retry=-", doneLine);                                   // the failure stays visible next to the apply
        Assert.Contains($"retry={(_time.GetUtcNow() + TimeSpan.FromMinutes(15)):O}", doneLine);
        Assert.Equal(2, h.Service.FailureStage);
        Assert.Equal(_time.GetUtcNow() + Interval, h.Service.NextDueUtc);

        // Still offline: the next interval-due tick keeps rotating on the normal cadence, retries in between never do.
        string afterFirst = h.State.CurrentImageId!;
        await AdvanceAsync(h, Interval);
        Assert.Equal(2, LogCount("tick reason=Interval"));
        Assert.Equal(2, h.Applier.Calls);
        Assert.False(string.Equals(afterFirst, h.State.CurrentImageId, StringComparison.Ordinal));
        Assert.Equal(1, LogCount("tick reason=Retry"));
        Assert.Equal(1, LogCount("why=retry"));
    }

    [Fact]
    public async Task Offline_Newest_IntervalTick_LeavesDesktopUntouched()
    {
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(Newest(), SeenState(dueInMinutes: 2), cache, Offline());
        await StartAsync(h);

        await AdvanceAsync(h, TimeSpan.FromMinutes(2));

        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.Contains("tick done reason=Interval result=FetchFailed decision=NoOp why=unchanged", LogText());
        Assert.Equal(_time.GetUtcNow() + Interval, h.Service.NextDueUtc);   // the schedule still moves on (no every-minute retry)

        await AdvanceAsync(h, Interval);
        Assert.Equal(2, LogCount("tick reason=Interval"));
        Assert.Equal(0, h.Applier.Calls);
    }

    [Fact]
    public async Task Offline_SingleImage_Random_BehavesAsNewest()
    {
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20")], applied: NewestId);
        Harness h = Build(RandomMode(), SeenState(dueInMinutes: 2), cache, Offline());
        await StartAsync(h);

        await AdvanceAsync(h, TimeSpan.FromMinutes(2));
        await AdvanceAsync(h, Interval);

        Assert.Equal(2, LogCount("tick reason=Interval"));
        Assert.Equal(2, LogCount("tick done reason=Interval result=FetchFailed decision=NoOp why=unchanged"));
        Assert.Equal(0, h.Applier.Calls);                    // ROT-03: one cached image, random behaves as newest
        Assert.Equal(NewestId, h.State.CurrentImageId);
    }

    [Fact]
    public async Task NetworkReturns_FirstSuccessAppliesNewest_ResetsStage()
    {
        // The desktop shows an older image (the last one seen before going offline); the fixture catalog's newest is
        // AlphornBavaria, which the first successful fetch must apply (CACHE-04, D-14).
        var network = new Network { Online = false };
        ImageCache cache = SeedCache([Cached(OldestId, "2026-09-14")], applied: OldestId);
        Harness h = Build(Newest(), SeenState(current: OldestId), cache, Switchable(network));
        await StartAsync(h);
        await AdvanceAsync(h, TimeSpan.FromMinutes(5));      // Startup + first Retry both fail
        Assert.Equal(2, h.Service.FailureStage);
        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(T0 + TimeSpan.FromMinutes(20), h.Service.RetryDueUtc);

        network.Online = true;
        await AdvanceAsync(h, TimeSpan.FromMinutes(15));     // the second Retry tick is the first one that can fetch

        Assert.Equal(2, LogCount("tick reason=Retry"));
        Assert.Equal(1, h.Applier.Calls);
        Assert.Contains("AlphornBavaria_EN-US6200857270", h.Applier.LastPath!);
        Assert.Equal(NewestId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(NewestId, h.State.LastSeenNewestId, StringComparer.Ordinal);
        Assert.Equal(0, h.Service.FailureStage);
        Assert.Null(h.Service.RetryDueUtc);
        Assert.Contains("tick done reason=Retry result=Applied decision=ApplyNew why=new", LogText());
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);   // a Retry tick applies but never re-arms the schedule
        Assert.Equal(NewestId, SavedState()!.LastSeenNewestId);
        AssertStateFileHasNoRetry();
    }

    [Fact]
    public async Task NetworkAvailable_PullsRetryToDebounce()
    {
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(Newest(), SeenState(), cache, Offline());
        await StartAsync(h);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);

        h.Service.OnNetworkAvailable();

        Assert.Equal(_time.GetUtcNow() + ScheduleMath.ResumeDebounce, h.Service.RetryDueUtc);
        Assert.Contains("schedule nudge source=network", LogText());
        Assert.Equal(1, h.Service.FailureStage);              // only the due time moves; the ladder position is unchanged
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
    }

    [Fact]
    public async Task NetworkAvailable_RetryRunsOnTheNudgedBeat_BeatExact()
    {
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(Newest(), SeenState(), cache, Offline());
        await StartAsync(h);
        h.Service.OnNetworkAvailable();
        DateTimeOffset pulled = h.Service.RetryDueUtc!.Value;

        // One second before the nudged beat: nothing has fired, the pulled-forward time is untouched.
        await AdvanceAsync(h, ScheduleMath.ResumeDebounce - TimeSpan.FromSeconds(1));
        Assert.Equal(0, LogCount("tick reason=Retry"));
        Assert.Equal(pulled, h.Service.RetryDueUtc);

        // Land exactly on the beat (never past it): the beat re-armed by the nudge must itself find the retry due,
        // which only holds while the pull-forward lead is <= the debounce the nudge used.
        await AdvanceAsync(h, TimeSpan.FromSeconds(1));
        Assert.Equal(pulled, _time.GetUtcNow());
        Assert.Equal(1, LogCount("tick reason=Retry"));
        Assert.Equal(2, h.Service.FailureStage);
    }

    [Fact]
    public async Task Offline_LastCheckUtc_AdvancesOnFailedAttempts()
    {
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(Newest(), SeenState(), cache, Offline());

        await StartAsync(h);
        Assert.Equal(T0, h.State.LastCheckUtc);            // RESEARCH A4: every attempt counts as a check
        Assert.Equal(T0, SavedState()!.LastCheckUtc);

        await AdvanceAsync(h, TimeSpan.FromMinutes(5));
        Assert.Equal(1, LogCount("tick reason=Retry"));
        Assert.Equal(_time.GetUtcNow(), h.State.LastCheckUtc);
        Assert.Equal(_time.GetUtcNow(), SavedState()!.LastCheckUtc);
    }

    [Fact]
    public async Task EmptyCache_NoCurrent_Offline_NoOpEmptyCache_NoThrow()
    {
        Harness h = Build(Newest(), new AppState(), SeedCache([]), Offline());

        TickResult result = await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);

        Assert.Equal(TickResult.FetchFailed, result);
        Assert.Equal(0, h.Applier.Calls);
        Assert.Null(h.State.CurrentImageId);
        Assert.Contains("tick done reason=Startup result=FetchFailed decision=NoOp why=empty-cache", LogText());
        Assert.Equal(0, LogCount("tick failed"));
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);   // the ladder still arms so the first fetch is retried soon
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
    }

    // ---- Plan 02-02 Task 2: clock jumps, DST, ApplySettings, startup jitter, the next-due invariant --------

    [Fact]
    public async Task BackwardClockJump_3h_ClampsNextDueToNowPlusInterval()
    {
        Harness h = Build(Newest(), new AppState());
        await StartAsync(h);                                 // applies at T0, NextDueUtc = T0 + 30 min
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        h.Applier.Reset();

        _time.AdjustTime(_time.GetUtcNow() - TimeSpan.FromHours(3));   // RESEARCH Pitfall 2: never SetUtcNow backwards
        h.Service.OnClockChanged();

        DateTimeOffset now = _time.GetUtcNow();
        Assert.Equal(T0 - TimeSpan.FromHours(3), now);
        Assert.Equal(now + Interval, h.Service.NextDueUtc);   // D-07: never more than one interval ahead
        Assert.Equal(now + Interval, SavedState()!.NextDueUtc);
        Assert.Contains("schedule nudge source=time-changed", LogText());

        await AdvanceAsync(h, Interval);                     // exactly one catch-up on the clamped schedule

        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(0, h.Applier.Calls);                    // unchanged catalog
        Assert.Equal(_time.GetUtcNow() + Interval, h.Service.NextDueUtc);
    }

    [Fact]
    public async Task BackwardClockJump_WithoutTimeChangedEvent_HeartbeatClamps()
    {
        Harness h = Build(Newest(), new AppState());
        await StartAsync(h);
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);

        _time.AdjustTime(_time.GetUtcNow() - TimeSpan.FromHours(3));
        Assert.True(h.Service.NextDueUtc!.Value - _time.GetUtcNow() > Interval);   // 3 h 30 min ahead until a beat runs

        await AdvanceAsync(h, TimeSpan.FromSeconds(60));     // the heartbeat alone must be sufficient (D-07, no TimeChanged bridge)

        DateTimeOffset now = _time.GetUtcNow();
        Assert.True(h.Service.NextDueUtc!.Value - now <= Interval);
        Assert.Equal(now + Interval, h.Service.NextDueUtc);
        Assert.Equal(0, LogCount("tick reason=Interval"));
    }

    [Fact]
    public async Task ForwardClockJump_3h_OneCatchUpTick()
    {
        Harness h = Build(Newest(), new AppState());
        await StartAsync(h);
        h.Applier.Reset();

        _time.AdjustTime(_time.GetUtcNow() + TimeSpan.FromHours(3));   // no timer fires on the jump itself
        Assert.Equal(0, LogCount("tick reason=Interval"));

        await AdvanceAsync(h, TimeSpan.FromSeconds(60));     // the next beat finds the schedule 2 h 30 min overdue

        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(_time.GetUtcNow() + Interval, h.Service.NextDueUtc);   // now + interval, not nextDue + n * interval
        Assert.Equal(0, h.Applier.Calls);
    }

    [Fact]
    public async Task Dst_FallBack_ServiceRearmsExactlyTwoHoursUtc()
    {
        // CEST -> CET at 2026-10-25T01:00Z: the local day has 25 hours, the interval is still 2 h of UTC.
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time"));
        var start = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        _time.SetUtcNow(start);
        Harness h = Build(new Settings { IntervalMinutes = 120, Mode = Settings.DefaultMode }, new AppState());

        await StartAsync(h);

        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.Zero), h.Service.NextDueUtc);
        Assert.Equal(TimeSpan.FromHours(2), h.Service.NextDueUtc!.Value - start);

        await AdvanceAsync(h, TimeSpan.FromHours(2));

        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(2), h.Service.NextDueUtc);
    }

    [Fact]
    public async Task ApplySettings_IntervalChange_RecomputesFromLastApplied()
    {
        Settings settings = Newest();
        Harness h = Build(settings, new AppState());
        await StartAsync(h);                                 // applies at T0: LastAppliedUtc == T0 (fake now)
        Assert.Equal(T0, h.State.LastAppliedUtc);
        await AdvanceAsync(h, TimeSpan.FromMinutes(10));
        Assert.Equal(0, LogCount("tick reason=Interval"));

        settings.IntervalMinutes = 60;                       // Phase 3 mutates the shared object, saves settings.json, then calls in
        h.Service.ApplySettings();

        Assert.Equal(T0 + TimeSpan.FromMinutes(60), h.Service.NextDueUtc);   // LastAppliedUtc + new interval, not now + interval
        Assert.Equal(T0 + TimeSpan.FromMinutes(60), SavedState()!.NextDueUtc);
        Assert.Contains($"settings applied interval=60 mode=newest resolution=UHD market=en-US monitors=same language=auto next={(T0 + TimeSpan.FromMinutes(60)):O}", LogText());
        Assert.Contains("schedule nudge source=settings", LogText());

        await AdvanceAsync(h, TimeSpan.FromMinutes(50));     // reaches T0 + 60 min: the recomputed schedule fires once
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(60), h.Service.NextDueUtc);
    }

    [Fact]
    public async Task ApplySettings_IntervalChange_ClampsToNowPlus5s_WhenAlreadyPast()
    {
        var settings = new Settings { IntervalMinutes = 120, Mode = Settings.DefaultMode };
        Harness h = Build(settings, new AppState());
        await StartAsync(h);                                 // applies at T0
        h.Applier.Reset();
        await AdvanceAsync(h, TimeSpan.FromHours(3));        // one interval tick at the 2 h mark, catalog unchanged -> NoOp
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(T0, h.State.LastAppliedUtc);

        settings.IntervalMinutes = 30;                       // T0 + 30 min is long past
        h.Service.ApplySettings();

        DateTimeOffset now = _time.GetUtcNow();
        Assert.Equal(now + ScheduleMath.MinLeadAfterIntervalChange, h.Service.NextDueUtc);   // never in the past, never immediate
        Assert.Contains("settings applied interval=30 mode=newest", LogText());

        await AdvanceAsync(h, ScheduleMath.ResumeDebounce + TimeSpan.FromSeconds(5));   // the nudged beat finds it due

        Assert.Equal(2, LogCount("tick reason=Interval"));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(30), h.Service.NextDueUtc);
    }

    [Fact]
    public async Task ApplySettings_SameInterval_DoesNotMoveNextDue()
    {
        Settings settings = Newest();
        Harness h = Build(settings, new AppState());
        await StartAsync(h);
        await AdvanceAsync(h, TimeSpan.FromMinutes(10));

        h.Service.ApplySettings();

        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        Assert.Contains($"settings applied interval=30 mode=newest resolution=UHD market=en-US monitors=same language=auto next={(T0 + Interval):O}", LogText());
        Assert.Contains("schedule nudge source=settings", LogText());
    }

    [Fact]
    public async Task ApplySettings_ModeChange_NextTickUsesNewMode()
    {
        Settings settings = Newest();
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(settings, SeenState(), cache);
        await StartAsync(h);                                 // not due, catalog unchanged
        Assert.Equal(0, h.Applier.Calls);

        settings.Mode = Settings.RandomMode;
        h.Service.ApplySettings();
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);   // a mode change alone never moves the schedule
        Assert.Contains("settings applied interval=30 mode=random", LogText());

        await AdvanceAsync(h, Interval);                     // the next tick reads the mode live: random rotates

        Assert.Equal(1, h.Applier.Calls);
        Assert.False(string.Equals(NewestId, h.State.CurrentImageId, StringComparison.Ordinal));
        Assert.Contains("tick done reason=Interval result=Applied decision=Random why=interval", LogText());

        settings.Mode = Settings.DefaultMode;
        h.Service.ApplySettings();
        await AdvanceAsync(h, Interval);                     // back in newest mode the unchanged catalog is a no-op

        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(2, LogCount("tick reason=Interval"));
        Assert.Contains("tick done reason=Interval result=NoOp decision=NoOp why=unchanged", LogText());
    }

    [Fact]
    public void Ctor_IntervalOutsideAllowed_Throws()
    {
        // Settings.LoadOrCreate sanitises the file, so an unlisted interval here is a programming error (WR-02).
        var settings = new Settings { IntervalMinutes = 0, Mode = Settings.DefaultMode };

        Assert.Throws<ArgumentOutOfRangeException>(() => Build(settings, new AppState()));
    }

    [Fact]
    public async Task ApplySettings_IntervalOutsideAllowed_LogsKeepsScheduleAndHeartbeatSurvives()
    {
        Settings settings = Newest();
        Harness h = Build(settings, new AppState());
        await StartAsync(h);                                 // applies at T0, NextDueUtc = T0 + 30 min
        h.Applier.Reset();

        settings.IntervalMinutes = 0;                        // a value the settings window must never produce (WR-02)
        h.Service.ApplySettings();                           // used to throw here, and the next beat used to kill the process

        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);   // the schedule keeps the last accepted interval
        Assert.Contains("settings rejected field=IntervalMinutes value=0 keeping=30", LogText());
        Assert.Contains($"settings applied interval=30 mode=newest resolution=UHD market=en-US monitors=same language=auto next={(T0 + Interval):O}", LogText());

        await AdvanceAsync(h, Interval);                     // 30 beats and the interval-due tick, all on the applied interval

        Assert.Equal(0, LogCount("heartbeat failed"));
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Contains("tick reason=Interval mode=newest interval=30", LogText());
        Assert.Equal(_time.GetUtcNow() + Interval, h.Service.NextDueUtc);   // re-armed with 30 min, never with 0

        settings.IntervalMinutes = 60;                       // a listed value is accepted again
        h.Service.ApplySettings();

        Assert.Equal(T0 + TimeSpan.FromMinutes(60), h.Service.NextDueUtc);   // LastAppliedUtc (T0) + 60 min
        Assert.Contains("settings applied interval=60 mode=newest", LogText());
    }

    [Fact]
    public async Task Startup_WithJitter_NoTickBeforeDelay_ThenExactlyOne()
    {
        Harness h = Build(Newest(), new AppState());

        h.Service.Start(TimeSpan.FromSeconds(45), CancellationToken.None);   // the --startup path (D-12)
        await AdvanceAsync(h, TimeSpan.FromSeconds(44));

        Assert.Empty(h.Http.Requests);                       // no network activity before the delay elapses
        Assert.Equal(0, LogCount("tick reason="));
        Assert.Equal(0, h.Applier.Calls);

        await AdvanceAsync(h, TimeSpan.FromSeconds(2));

        Assert.Equal(1, LogCount("tick reason=Startup"));
        Assert.Equal(1, LogCount("tick reason="));
        Assert.Equal(1, h.Applier.Calls);
        Assert.NotEmpty(h.Http.Requests);
        Assert.Equal(_time.GetUtcNow() + Interval, h.Service.NextDueUtc);
    }

    [Fact]
    public async Task Invariant_NextDueMinusNow_NeverExceedsInterval()
    {
        Settings settings = Newest();
        ImageCache cache = SeedCache(ThreeCached(), applied: NewestId);
        Harness h = Build(settings, SeenState(), cache);
        await StartAsync(h);
        AssertInvariant(h, settings);

        for (int i = 0; i < 20; i++)
        {
            switch (i % 4)
            {
                case 0:
                    await AdvanceAsync(h, TimeSpan.FromMinutes(7));
                    break;
                case 1:
                    settings.IntervalMinutes = settings.IntervalMinutes == 30 ? 60 : 30;
                    h.Service.ApplySettings();
                    await h.Service.WaitForIdleAsync();
                    break;
                case 2:
                    _time.AdjustTime(_time.GetUtcNow() - TimeSpan.FromHours(1));   // what the TimeChanged bridge sees
                    h.Service.OnClockChanged();
                    await h.Service.WaitForIdleAsync();
                    break;
                default:
                    Assert.Equal(TickResult.Applied, await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None));
                    break;
            }

            AssertInvariant(h, settings);
        }

        Assert.Equal(5, LogCount("decision=StepOlder why=next"));
        Assert.Equal(5, LogCount("schedule nudge source=time-changed"));
        Assert.Equal(5, LogCount("settings applied interval="));
    }

    private void AssertInvariant(Harness h, Settings settings)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Assert.NotNull(h.Service.NextDueUtc);
        TimeSpan lead = h.Service.NextDueUtc!.Value - now;
        Assert.True(lead <= settings.Interval, $"NextDueUtc - now = {lead} exceeds the interval {settings.Interval} at {now:O}");
    }

    // ---- Plan 02-05 (gap G-01 / CR-01): the exception path of a tick ----------------------------------

    /// <summary>
    /// Three images cached with the middle one on the desktop and the catalog already seen: the Startup tick is a
    /// not-due NoOp. The README then gains a row newer than everything cached (<see cref="FreshId"/>, with a new
    /// ETag so the next conditional GET is a 200), so the interval-due tick decides ApplyNew for an image it has to
    /// download first. <c>LastSeenNewestId</c> is never written by the test: it stays on the service's own D-03 path.
    /// </summary>
    private async Task<Harness> StartNotDueWithMiddleApplied_ThenCatalogMoved(ImageCache? cache = null)
    {
        cache ??= SeedCache(ThreeCached(), applied: MiddleId);
        var state = new AppState { CurrentImageId = MiddleId, LastSeenNewestId = NewestId, NextDueUtc = T0 + Interval };
        var catalog = new Catalog();
        Harness h = Build(Newest(), state, cache, Routes(catalog));

        await StartAsync(h);
        Assert.Equal(0, h.Applier.Calls);
        Assert.Contains("tick done reason=Startup result=NoOp decision=NoOp why=not-due", LogText());

        catalog.Body = MovedReadme();   // no tick is running; the fake clock only fires inside Advance
        return h;
    }

    [Fact]
    public async Task Interval_ApplyThrows_RearmsAndDoesNotRetryEveryMinute()
    {
        Harness h = await StartNotDueWithMiddleApplied_ThenCatalogMoved();
        h.Applier.ThrowNext = new InvalidOperationException("simulated COM failure");

        await AdvanceAsync(h, Interval);   // the interval-due tick at T0 + 30 min: FreshId downloaded, applier throws

        DateTimeOffset now = _time.GetUtcNow();
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(1, h.Applier.Calls);
        Assert.Contains("ParisSunset", h.Applier.LastPath!, StringComparison.Ordinal);
        Assert.Equal(1, LogCount("cache add id=" + FreshId));
        Assert.Contains("tick failed reason=Interval error=simulated COM failure", LogText());
        Assert.Contains("tick done reason=Interval result=Failed decision=ApplyNew why=new", LogText());
        Assert.Equal(NewestId, h.State.LastSeenNewestId, StringComparer.Ordinal);   // written only after a successful apply (D-03)
        Assert.Equal(MiddleId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(new[] { MiddleId }, h.Cache.Index.Applied);          // ApplyStage rolled the record back before rethrowing
        Assert.Equal(now + Interval, h.Service.NextDueUtc);                 // re-armed despite the throw (G-01)
        Assert.Equal(now + Interval, SavedState()!.NextDueUtc);             // and the tail persisted it
        Assert.Equal(1, h.Service.FailureStage);                            // a throw takes the same ladder as a failed fetch (D-13)
        Assert.Equal(now + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);
        Assert.Contains($"retry scheduled stage=1 at={(now + TimeSpan.FromMinutes(5)):O}", LogText());
        AssertStateFileHasNoRetry();

        int requests = h.Http.Requests.Count;
        await AdvanceAsync(h, TimeSpan.FromMinutes(4));   // four beats inside the retry lead: no tick, no network

        Assert.Equal(2, LogCount("tick reason="));       // Startup + the one Interval
        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(requests, h.Http.Requests.Count);

        await AdvanceAsync(h, TimeSpan.FromMinutes(1));   // the ladder-due beat: exactly one Retry, the one-shot throw is spent

        Assert.Equal(1, LogCount("tick reason=Retry"));
        Assert.Equal(2, h.Applier.Calls);
        Assert.Equal(1, LogCount("cache add id=" + FreshId));             // the retry is a cache hit: the image is newer than the last-seen one, so it is still "new" (WR-01)
        Assert.Equal(FreshId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(FreshId, h.State.LastSeenNewestId, StringComparer.Ordinal);
        Assert.Contains("tick done reason=Retry result=Applied decision=ApplyNew why=new", LogText());
        Assert.Equal(0, h.Service.FailureStage);
        Assert.Null(h.Service.RetryDueUtc);
        Assert.Equal(T0 + Interval + Interval, h.Service.NextDueUtc);      // a Retry never re-arms
    }

    [Fact]
    public async Task Interval_ApplyFails_RearmsAndDoesNotRetryEveryMinute()
    {
        // The return-value failure (IN-07): the fetch succeeded, so an apply failure is retried on the interval and
        // never on the ladder — D-13 is about fetches (the 02-01 rule).
        Harness h = await StartNotDueWithMiddleApplied_ThenCatalogMoved();
        h.Applier.FailNext = true;

        await AdvanceAsync(h, Interval);

        DateTimeOffset now = _time.GetUtcNow();
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(1, h.Applier.Calls);
        Assert.Contains("apply failed method=fake error=forced failure", LogText());
        Assert.Contains("tick done reason=Interval result=ApplyFailed decision=ApplyNew why=new", LogText());
        Assert.Equal(0, LogCount("tick failed"));
        Assert.Equal(NewestId, h.State.LastSeenNewestId, StringComparer.Ordinal);   // not advanced by a failed apply (D-03)
        Assert.Equal(MiddleId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(new[] { MiddleId }, h.Cache.Index.Applied);
        Assert.Equal(now + Interval, h.Service.NextDueUtc);
        Assert.Equal(now + Interval, SavedState()!.NextDueUtc);
        Assert.Equal(0, h.Service.FailureStage);
        Assert.Null(h.Service.RetryDueUtc);

        await AdvanceAsync(h, TimeSpan.FromMinutes(5));   // where a ladder retry would have fired: nothing

        Assert.Equal(2, LogCount("tick reason="));
        Assert.Equal(1, h.Applier.Calls);

        await AdvanceAsync(h, Interval - TimeSpan.FromMinutes(5));   // the next interval-due tick applies the image (cache hit)

        Assert.Equal(2, LogCount("tick reason=Interval"));
        Assert.Equal(2, h.Applier.Calls);
        Assert.Equal(1, LogCount("cache add id=" + FreshId));
        Assert.Equal(FreshId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(FreshId, h.State.LastSeenNewestId, StringComparer.Ordinal);
        Assert.Contains("tick done reason=Interval result=Applied decision=ApplyNew why=new", LogText());
    }

    [Fact]
    public async Task Startup_ApplyThrows_NotReplayedEveryBeat()
    {
        // Fresh state, empty cache: the Startup tick is due, downloads the newest image and its applier throws. The
        // dispatch-time flag means the heartbeat never replays Startup; the ladder (not the heartbeat) recovers it.
        Harness h = Build(Newest(), new AppState());
        h.Applier.ThrowNext = new InvalidOperationException("simulated COM failure");

        await StartAsync(h);

        Assert.Equal(1, LogCount("tick reason=Startup"));
        Assert.Equal(1, h.Applier.Calls);
        Assert.Contains("tick failed reason=Startup error=simulated COM failure", LogText());
        Assert.Contains("tick done reason=Startup result=Failed decision=ApplyNew why=new", LogText());
        Assert.Null(h.State.CurrentImageId);
        Assert.Null(h.State.LastSeenNewestId);
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        Assert.Equal(T0 + Interval, SavedState()!.NextDueUtc);
        Assert.Equal(1, h.Service.FailureStage);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);

        await AdvanceAsync(h, TimeSpan.FromMinutes(4));   // four beats: no Startup replay, no Interval, no Retry

        Assert.Equal(1, LogCount("tick reason=Startup"));
        Assert.Equal(1, LogCount("tick reason="));
        Assert.Equal(1, h.Applier.Calls);

        await AdvanceAsync(h, TimeSpan.FromMinutes(1));   // the ladder-due beat recovers

        Assert.Equal(1, LogCount("tick reason=Retry"));
        Assert.Equal(2, h.Applier.Calls);
        Assert.Equal(NewestId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(NewestId, h.State.LastSeenNewestId, StringComparer.Ordinal);
        Assert.Equal(0, h.Service.FailureStage);
        Assert.Null(h.Service.RetryDueUtc);
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
    }

    [Fact]
    public async Task Interval_EnsureSaveThrows_FollowsLadder_ThenRecovers()
    {
        // The most realistic trigger: index.json cannot be written. AtomicJsonFile.Save opens <path>.tmp with
        // FileMode.Create, so a directory in that place makes the FileStream constructor throw
        // (UnauthorizedAccessException — the same family as a locked or read-only index.json). The throw site is
        // ImageCache.Add -> Save() at the end of EnsureAsync, after the download succeeded and before the applier.
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")], applied: MiddleId);   // FreshId is NOT cached
        Harness h = await StartNotDueWithMiddleApplied_ThenCatalogMoved(cache);
        string tmpBlocker = _indexPath + ".tmp";
        Directory.CreateDirectory(tmpBlocker);

        await AdvanceAsync(h, Interval);   // T0 + 30: the interval-due tick downloads, then Add's Save throws

        DateTimeOffset now1 = _time.GetUtcNow();
        Assert.Equal(1, LogCount("tick reason=Interval"));
        Assert.Equal(0, h.Applier.Calls);                  // the applier was never reached
        Assert.Equal(0, LogCount("cache add id=" + FreshId));   // Add threw before its log line (CACHE-06: the startup backfill of NewestId is the only cache add)
        Assert.Equal(1, LogCount("cache add"));            // CACHE-06: +1 backfill after the successful (not-due) startup tick
        Assert.Contains("tick failed reason=Interval error=", LogText());
        Assert.Contains("tick done reason=Interval result=Failed decision=ApplyNew why=new", LogText());
        Assert.Equal(NewestId, h.State.LastSeenNewestId, StringComparer.Ordinal);   // untouched: nothing was applied (D-03)
        Assert.Equal(MiddleId, h.State.CurrentImageId, StringComparer.Ordinal);
        CacheIndex onDisk = AtomicJsonFile.Load(_indexPath, CoreJsonContext.Default.CacheIndex)!;
        Assert.Equal(new[] { MiddleId }, onDisk.Applied);
        Assert.DoesNotContain(onDisk.Images, i => string.Equals(i.Id, FreshId, StringComparison.Ordinal));   // the failed Add never reached disk
        // IN-09: memory matches disk after the failed save, and the unrecorded download is not left as an orphan.
        Assert.DoesNotContain(h.Cache.Index.Images, i => string.Equals(i.Id, FreshId, StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_dir, "*ParisSunset*"));
        Assert.Equal(2, ImageDownloads(h));               // CACHE-06: +1 backfill after the successful startup tick
        Assert.Equal(now1 + Interval, h.Service.NextDueUtc);
        Assert.Equal(now1 + Interval, SavedState()!.NextDueUtc);   // state.json has its own .tmp, so the tail's save works
        Assert.Equal(1, h.Service.FailureStage);
        Assert.Equal(now1 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);

        await AdvanceAsync(h, TimeSpan.FromMinutes(4));
        Assert.Equal(2, LogCount("tick reason="));

        await AdvanceAsync(h, TimeSpan.FromMinutes(1));   // first retry, the disk is still unwritable

        Assert.Equal(1, LogCount("tick reason=Retry"));
        Assert.Equal(1, LogCount("tick failed reason=Retry"));
        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(3, ImageDownloads(h));               // not a cache hit: the unrecorded image is downloaded again (IN-09); CACHE-06: +1 startup backfill
        Assert.DoesNotContain(h.Cache.Index.Images, i => string.Equals(i.Id, FreshId, StringComparison.Ordinal));
        Assert.Equal(2, h.Service.FailureStage);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(15), h.Service.RetryDueUtc);
        Assert.Equal(now1 + Interval, h.Service.NextDueUtc);   // a Retry never re-arms
        AssertStateFileHasNoRetry();

        await AdvanceAsync(h, TimeSpan.FromMinutes(14));
        Assert.Equal(3, LogCount("tick reason="));

        Directory.Delete(tmpBlocker);
        await AdvanceAsync(h, TimeSpan.FromMinutes(1));   // second retry, the disk is writable again

        Assert.Equal(2, LogCount("tick reason=Retry"));
        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(FreshId, h.State.CurrentImageId, StringComparer.Ordinal);
        Assert.Equal(FreshId, h.State.LastSeenNewestId, StringComparer.Ordinal);
        Assert.Contains("tick done reason=Retry result=Applied decision=ApplyNew why=new", LogText());
        Assert.Equal(0, h.Service.FailureStage);
        Assert.Null(h.Service.RetryDueUtc);
        Assert.Equal(now1 + Interval, h.Service.NextDueUtc);
        Assert.Equal(5, ImageDownloads(h));               // CACHE-06: +1 startup backfill, +1 backfill after the successful Retry tick
        Assert.Equal(1, LogCount("cache add id=" + FreshId));
        onDisk = AtomicJsonFile.Load(_indexPath, CoreJsonContext.Default.CacheIndex)!;
        Assert.Equal(new[] { FreshId }, onDisk.Applied);
        Assert.Contains(onDisk.Images, i => string.Equals(i.Id, FreshId, StringComparison.Ordinal));
    }

    private static int ImageDownloads(Harness h) =>
        h.Http.Requests.Count(r => r.Uri.AbsolutePath.StartsWith(ImagePath, StringComparison.Ordinal));

    [Fact]
    public async Task Tick_ApplyThrowsNonTokenCancellation_IsFailedNotCancelled()
    {
        // WR-04: a TaskCanceledException that is not the tick's own token (HttpClient.Timeout, Task.WaitAsync(TimeSpan),
        // a linked-CTS timeout) is a failure like any other throw — re-arm, ladder, save — never a Cancelled that leaves
        // NextDueUtc in the past for the heartbeat to re-dispatch every minute.
        Harness h = Build(Newest(), new AppState());
        h.Applier.ThrowNext = new TaskCanceledException("simulated timeout");

        TickResult result = await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);

        Assert.Equal(TickResult.Failed, result);
        Assert.Equal(1, h.Applier.Calls);
        Assert.Contains("tick failed reason=Startup error=simulated timeout", LogText());
        Assert.Contains("tick done reason=Startup result=Failed decision=ApplyNew why=new", LogText());
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        Assert.Equal(T0 + Interval, SavedState()!.NextDueUtc);
        Assert.Equal(1, h.Service.FailureStage);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);
        Assert.Null(h.State.LastSeenNewestId);
    }

    [Fact]
    public async Task Tick_GatewayTimeoutCancellation_IsFetchFailedNotCancelled()
    {
        // Pins the filtering the lower layers do today (HttpGateway / RetryPolicy / CatalogService turn a timeout-flavoured
        // TaskCanceledException into a failed source before it reaches the tick); should that ever move, the WR-04
        // `when (ct.IsCancellationRequested)` clauses still keep the tick on the failure path with its tail intact.
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20")], applied: NewestId);
        var timeouts = new FakeHttpHandler(FakeHttpHandler.Throw(new TaskCanceledException("simulated timeout")));
        Harness h = Build(Newest(), SeenState(dueInMinutes: null), cache, timeouts);

        TickResult result = await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);

        Assert.NotEqual(TickResult.Cancelled, result);
        Assert.True(result is TickResult.FetchFailed or TickResult.Failed, result.ToString());
        Assert.Equal(1, LogCount("tick done reason=Startup"));   // the re-arm / ladder / save tail ran
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        Assert.Equal(T0 + Interval, SavedState()!.NextDueUtc);
        Assert.Equal(1, h.Service.FailureStage);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);
        Assert.Equal(0, h.Applier.Calls);
    }

    [Fact]
    public async Task Tick_CancelledMidApply_ReturnsCancelled_LeavesScheduleAlone()
    {
        // The rethrow clause: cancellation is not a failure. A tick treated as Failed would have re-armed to T0 + 30 min.
        var dispatcher = new BlockingUiDispatcher();
        Harness h = Build(Newest(), new AppState(), dispatcher: dispatcher);
        using var cts = new CancellationTokenSource();

        Task<TickResult> tick = h.Service.RunTickAsync(TickReason.Startup, cts.Token);
        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        Assert.Equal(TickResult.Cancelled, await tick.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(h.State.NextDueUtc);
        Assert.Equal(0, LogCount("tick done"));
        Assert.Equal(0, LogCount("tick failed"));
        Assert.Equal(0, h.Applier.Calls);
        Assert.False(h.Service.IsTickRunning);

        dispatcher.Release();   // nothing is left parked
    }

    // ---- Phase 3: Snapshot() and StateChanged for the Settings window (UI-04) -------------------------

    [Fact]
    public void Snapshot_NoCurrentImage_ReturnsBlankMetadataAndNullTimes()
    {
        Harness h = Build(Newest(), new AppState());

        RotationSnapshot s = h.Service.Snapshot();

        Assert.Null(s.Title);
        Assert.Null(s.Copyright);
        Assert.Null(s.Date);
        Assert.Null(s.LastCheckUtc);
        Assert.Null(s.NextDueUtc);
        Assert.False(s.TickRunning);
        Assert.Equal(LastErrorKind.None, s.LastError);
    }

    [Fact]
    public async Task Snapshot_AfterApply_ReturnsCachedTitleCopyrightDateAndTimes()
    {
        Harness h = Build(Newest(), new AppState());

        await StartAsync(h);
        RotationSnapshot s = h.Service.Snapshot();

        CachedImage cached = Assert.Single(h.Cache.Index.Images, i => i.Id == NewestId);
        Assert.Equal("The Alpine sound of Oktoberfest", cached.Title);   // enriched from the HPImageArchive fixture
        Assert.Equal(cached.Title, s.Title);
        Assert.Equal(cached.Copyright, s.Copyright);
        Assert.Equal(cached.Date, s.Date);
        Assert.Equal(T0, s.LastCheckUtc);
        Assert.Equal(T0 + Interval, s.NextDueUtc);
        Assert.False(s.TickRunning);
        Assert.Equal(LastErrorKind.None, s.LastError);
    }

    [Fact]
    public async Task Snapshot_AfterOfflineTick_LastErrorIsFetch_AndClearsAfterSuccess()
    {
        var network = new Network { Online = false };
        ImageCache cache = SeedCache([Cached(OldestId, "2026-09-14")], applied: OldestId);
        Harness h = Build(Newest(), SeenState(current: OldestId), cache, Switchable(network));

        await StartAsync(h);
        Assert.Equal(LastErrorKind.Fetch, h.Service.Snapshot().LastError);
        Assert.Equal(T0, h.Service.Snapshot().LastCheckUtc);   // Last checked advances even when the fetch fails

        network.Online = true;
        await AdvanceAsync(h, TimeSpan.FromMinutes(5));   // the first retry succeeds

        Assert.Equal(LastErrorKind.None, h.Service.Snapshot().LastError);
        Assert.Equal(NewestId, h.State.CurrentImageId);
    }

    [Fact]
    public async Task Snapshot_AfterFailedApply_LastErrorIsApply()
    {
        var applier = new FakeApplier { FailNext = true };
        Harness h = Build(Newest(), new AppState(), applier: applier);

        await StartAsync(h);

        Assert.Equal(1, applier.Calls);
        Assert.Equal(LastErrorKind.Apply, h.Service.Snapshot().LastError);
        Assert.Contains("tick done reason=Startup result=ApplyFailed", LogText());
    }

    [Fact]
    public async Task Snapshot_AfterThrowingApply_LastErrorIsApply()
    {
        var applier = new FakeApplier { ThrowNext = new InvalidOperationException("boom") };
        Harness h = Build(Newest(), new AppState(), applier: applier);

        await StartAsync(h);

        Assert.Equal(LastErrorKind.Apply, h.Service.Snapshot().LastError);
        Assert.Contains("tick done reason=Startup result=Failed", LogText());
    }

    [Fact]
    public async Task Snapshot_AfterReadBackMismatch_LastErrorIsReadBack()
    {
        var applier = new FakeApplier { ReadBackOverride = Path.Combine(_dir, "spotlight.jpg") };
        Harness h = Build(Newest(), new AppState(), applier: applier);

        await StartAsync(h);

        Assert.Equal(NewestId, h.State.CurrentImageId);   // the apply is still Ok for the pipeline (ApplyStage policy)
        Assert.Equal(LastErrorKind.ReadBack, h.Service.Snapshot().LastError);

        applier.ReadBackOverride = null;
        await AdvanceAsync(h, Interval);   // the next interval tick reads back its own path again

        Assert.Equal(LastErrorKind.None, h.Service.Snapshot().LastError);
    }

    [Fact]
    public async Task StateChanged_RaisedAfterTickAndAfterApplySettings_WithTickRunningFalse()
    {
        Harness h = Build(Newest(), new AppState());
        int raised = 0;
        int runningWhenRaised = 0;
        h.Service.StateChanged += () =>
        {
            Interlocked.Increment(ref raised);
            if (h.Service.Snapshot().TickRunning)
            {
                Interlocked.Increment(ref runningWhenRaised);
            }
        };

        // Awaited directly rather than through StartAsync: WaitForIdleAsync takes the gate itself the moment the tick
        // releases it, so a handler raised right after that release could read TickRunning == true from the test's
        // own hold, not from a tick (seen once on the CI runner). The awaited call returns after the finally's raise.
        await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        Assert.Equal(1, raised);

        h.Service.ApplySettings();
        Assert.Equal(2, raised);
        Assert.Equal(0, runningWhenRaised);   // raised only after the gate is released, never at acquisition
    }

    [Fact]
    public async Task StateChanged_HandlerThrows_TickStillCompletes()
    {
        Harness h = Build(Newest(), new AppState());
        h.Service.StateChanged += () => throw new InvalidOperationException("ui handler bug");

        TickResult result = await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);

        Assert.Equal(TickResult.Applied, result);
        Assert.Equal(1, h.Applier.Calls);
        Assert.False(h.Service.IsTickRunning);
        Assert.Contains("state changed handler failed", LogText());
        Assert.Contains("ui handler bug", LogText());
    }

    // ---- Phase 3 (03-02): Auto resolution is resolved once per tick, before the decider and the download stage (SRC-07) ----

    private static Settings AutoResolution() => new() { IntervalMinutes = 30, Mode = Settings.DefaultMode, Resolution = Settings.AutoResolution };

    private List<RecordedRequest> ImageRequests(Harness h) =>
        h.Http.Requests.Where(r => r.Uri.AbsolutePath.StartsWith(ImagePath, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// The invariant test for the promoted noun: under "Auto" the download URL carries the concrete w/h, the cache entry
    /// stores the concrete resolution, and "Auto" never reaches BingImageUrl.MinDimensions / CacheFileName (Pitfall 3).
    /// </summary>
    [Fact]
    public async Task Auto_ResolvesBeforeEnsure_NeverReachesDownloadOrCache()
    {
        var layout = new FakeMonitorLayout([(1920, 1080)]);
        Settings settings = AutoResolution();
        Harness h = Build(settings, new AppState(), monitors: layout);

        TickResult result = await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);

        Assert.Equal(TickResult.Applied, result);
        Assert.Equal(1, layout.Calls);
        Assert.Equal(Settings.AutoResolution, settings.Resolution);   // the setting itself is untouched; only the tick's local is concrete
        List<RecordedRequest> images = ImageRequests(h);
        Assert.Equal(2, images.Count);   // CACHE-06: +1 backfill after the successful tick
        Assert.All(images, image => Assert.Contains("w=1920&h=1080", image.Uri.Query, StringComparison.Ordinal));
        Assert.Equal(2, h.Cache.Index.Images.Count);   // CACHE-06: +1 backfill after the successful tick
        Assert.All(h.Cache.Index.Images, entry =>
        {
            Assert.Equal("1920x1080", entry.Resolution);
            Assert.EndsWith(".1920x1080.jpg", entry.File, StringComparison.Ordinal);
            Assert.DoesNotContain("Auto", entry.File, StringComparison.OrdinalIgnoreCase);
        });

        string log = LogText();
        Assert.Contains("tick reason=Startup mode=newest interval=30 resolution=1920x1080 auto=1920x1080", log);
        Assert.DoesNotContain("tick failed", log);
        Assert.DoesNotContain("unknown resolution", log);
    }

    [Fact]
    public async Task Auto_NoMonitors_UsesUhd()
    {
        Harness h = Build(AutoResolution(), new AppState(), monitors: new FakeMonitorLayout([]));

        TickResult result = await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);

        Assert.Equal(TickResult.Applied, result);
        List<RecordedRequest> images = ImageRequests(h);
        Assert.Equal(2, images.Count);   // CACHE-06: +1 backfill after the successful tick
        Assert.All(images, image => Assert.DoesNotContain("w=", image.Uri.Query, StringComparison.Ordinal));
        Assert.Equal(2, h.Cache.Index.Images.Count);   // CACHE-06: +1 backfill after the successful tick
        Assert.All(h.Cache.Index.Images, entry => Assert.Equal("UHD", entry.Resolution));
        Assert.Contains("resolution=UHD auto=-", LogText());
        Assert.DoesNotContain("tick failed", LogText());
    }

    [Fact]
    public async Task Auto_LayoutThrows_DegradesToUhd()
    {
        var layout = new FakeMonitorLayout([(3840, 2160)]) { Throw = new InvalidOperationException("screen enumeration bug") };
        Harness h = Build(AutoResolution(), new AppState(), monitors: layout);

        TickResult result = await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);

        Assert.Equal(TickResult.Applied, result);
        Assert.Equal(2, h.Cache.Index.Images.Count);   // CACHE-06: +1 backfill after the successful tick
        Assert.All(h.Cache.Index.Images, entry => Assert.Equal("UHD", entry.Resolution));
        string log = LogText();
        Assert.Contains("monitor layout failed", log);
        Assert.Contains("screen enumeration bug", log);
        Assert.Contains("resolution=UHD auto=-", log);
        Assert.DoesNotContain("tick failed", log);
    }

    /// <summary>The largest monitor by area wins, and an explicit setting ignores the layout entirely.</summary>
    [Fact]
    public async Task Auto_TwoMonitors_LargestWins_AndExplicitSettingIgnoresLayout()
    {
        var layout = new FakeMonitorLayout([(1920, 1080), (3840, 2160)]);
        Harness auto = Build(AutoResolution(), new AppState(), monitors: layout);
        await auto.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        Assert.Contains("resolution=UHD auto=3840x2160", LogText());
        Assert.Equal(2, auto.Cache.Index.Images.Count);   // CACHE-06: +1 backfill after the successful tick
        Assert.All(auto.Cache.Index.Images, entry => Assert.Equal("UHD", entry.Resolution));

        Settings explicitSetting = Newest();
        explicitSetting.Resolution = "1920x1200";
        Harness fixedRes = Build(explicitSetting, new AppState(), SeedCache([]), monitors: new FakeMonitorLayout([(3840, 2160)]));
        await fixedRes.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        Assert.Contains("resolution=1920x1200 auto=-", LogText());
        Assert.Equal(2, fixedRes.Cache.Index.Images.Count);   // CACHE-06: +1 backfill after the successful tick
        Assert.All(fixedRes.Cache.Index.Images, entry => Assert.Equal("1920x1200", entry.Resolution));
    }

    /// <summary>A resolution change in the window is used at the next tick with no restart (criterion 2): the setting is read live.</summary>
    [Fact]
    public async Task Auto_DisplayChangeBetweenTicks_NextTickUsesNewLayout()
    {
        var layout = new FakeMonitorLayout([(1920, 1080)]);
        Harness h = Build(AutoResolution(), new AppState(), monitors: layout);
        await h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        Assert.Contains("resolution=1920x1080 auto=1920x1080", LogText());

        layout.Sizes = [(2560, 1440)];
        await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None);

        Assert.Contains("tick reason=Next mode=newest interval=30 resolution=UHD auto=2560x1440", LogText());
    }

    // ---- Plan 03-04 Task 1: per-monitor apply (WALL-03) ----------------------------------------------

    private static readonly MonitorHandle LeftMonitor = new(@"\\?\DISPLAY#HKC0000#5&29c4b990&0&UID4355#{guid}", 1920, 1080, 0, 0);
    private static readonly MonitorHandle RightMonitor = new(@"\\?\DISPLAY#HKC0000#5&29c4b990&0&UID4356#{guid}", 1920, 1080, 1920, 0);
    private static readonly MonitorHandle ThirdMonitor = new(@"\\?\DISPLAY#DOCK0001#7&1a2b3c4d&0&UID9#{guid}", 2560, 1440, 3840, 0);

    private static Settings PerMonitor() => new() { IntervalMinutes = 30, Mode = Settings.DefaultMode, MonitorMode = Settings.PerMonitorMode };

    private static FakeApplier TwoMonitorApplier()
    {
        var applier = new FakeApplier();
        applier.Monitors.Add(LeftMonitor);
        applier.Monitors.Add(RightMonitor);
        return applier;
    }

    [Fact]
    public async Task PerMonitor_TwoMonitorsTwoImages_AppliesDistinctImages()
    {
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(PerMonitor(), new AppState(), cache, applier: TwoMonitorApplier());

        await StartAsync(h);

        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> call = Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(1, h.Applier.Calls);   // exactly one apply of any kind
        Assert.Equal(2, call.Count);
        Assert.Same(LeftMonitor, call[0].Monitor);
        Assert.Same(RightMonitor, call[1].Monitor);
        Assert.Contains("AlphornBavaria_EN-US6200857270", call[0].AbsolutePath);   // downloaded this tick (CacheFileName drops the OHR. prefix)
        Assert.Contains(MiddleId, call[1].AbsolutePath);                              // the seeded neighbour
        Assert.NotEqual(call[0].AbsolutePath, call[1].AbsolutePath);
        Assert.True(File.Exists(call[0].AbsolutePath));
        Assert.True(File.Exists(call[1].AbsolutePath));
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.Equal(NewestId, h.State.LastSeenNewestId);

        string log = LogText();
        Assert.Contains("apply ok method=fake id=", log);
        Assert.Contains($" monitors=2 ids={NewestId},{MiddleId} ", log);
        Assert.Contains("tick done reason=Startup result=Applied decision=ApplyNew why=new", log);
    }

    [Fact]
    public async Task PerMonitor_OneCachedImage_SameOnAll()
    {
        Harness h = Build(PerMonitor(), new AppState(), applier: TwoMonitorApplier());

        await StartAsync(h);

        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> call = Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(2, call.Count);
        Assert.Equal(call[0].AbsolutePath, call[1].AbsolutePath);
        Assert.Equal([NewestId], h.Cache.Index.Applied);
        Assert.Contains($" monitors=2 ids={NewestId} ", LogText());
    }

    [Fact]
    public async Task PerMonitor_NoAttachedMonitors_FallsBackToSingleApply()
    {
        Harness h = Build(PerMonitor(), new AppState());   // FakeApplier.Monitors is empty: no per-monitor support

        await StartAsync(h);

        Assert.Empty(h.Applier.PerMonitorCalls);
        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(1, h.Applier.MonitorEnumerations);
        Assert.Equal([NewestId], h.Cache.Index.Applied);
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.Contains($" monitors=0 ids={NewestId} ", LogText());
    }

    [Fact]
    public async Task SameMode_NeverCallsApplyPerMonitor()
    {
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(Newest(), new AppState(), cache, RoutesFor(NewestId, MiddleId), applier: TwoMonitorApplier());   // CACHE-06: catalog pinned to the seeded cache

        await StartAsync(h);
        await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None);   // steps to the older seeded image

        Assert.Empty(h.Applier.PerMonitorCalls);
        Assert.Equal(0, h.Applier.MonitorEnumerations);
        Assert.Equal(2, h.Applier.Calls);
        Assert.Equal(MiddleId, h.State.CurrentImageId);
        Assert.Single(h.Cache.Index.Applied);
        Assert.DoesNotContain(" monitors=2 ids=", LogText());
    }

    /// <summary>Next in per-monitor mode steps the primary to the next older image; the neighbour follows it (deterministic plan).</summary>
    [Fact]
    public async Task PerMonitor_Next_StepsPrimary_NeighbourFollows()
    {
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17", 60), Cached(OldestId, "2026-09-14", 0)]);
        Harness h = Build(PerMonitor(), new AppState(), cache, RoutesFor(NewestId, MiddleId, OldestId), applier: TwoMonitorApplier());   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);

        await h.Service.RunTickAsync(TickReason.Next, CancellationToken.None);

        Assert.Equal(2, h.Applier.PerMonitorCalls.Count);
        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> second = h.Applier.PerMonitorCalls[1];
        Assert.Contains(MiddleId, second[0].AbsolutePath);
        Assert.Contains(OldestId, second[1].AbsolutePath);
        Assert.Equal([MiddleId, OldestId], h.Cache.Index.Applied);
        Assert.Equal(MiddleId, h.State.CurrentImageId);
    }

    // ---- Plan 03-04 Task 2: dock/undock re-apply (debounced, gated, attached-set aware) and mode switch ------

    /// <summary>Per-monitor harness that has already applied over two monitors: one Newest download plus two seeded older images.</summary>
    private async Task<Harness> PerMonitorAppliedAsync()
    {
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17", 60), Cached(OldestId, "2026-09-14", 0)]);
        Harness h = Build(PerMonitor(), new AppState(), cache, RoutesFor(NewestId, MiddleId, OldestId), applier: TwoMonitorApplier());   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);
        Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);
        return h;
    }

    [Fact]
    public async Task OnDisplayChanged_PerMonitor_AttachedSetChanged_ReappliesOnceAfterDebounce()
    {
        Harness h = await PerMonitorAppliedAsync();
        int requestsBefore = h.Http.Requests.Count;
        DateTimeOffset? nextDueBefore = h.Service.NextDueUtc;
        DateTimeOffset? lastAppliedBefore = h.State.LastAppliedUtc;

        h.Applier.Monitors.Add(ThirdMonitor);   // docked a third display
        h.Service.OnDisplayChanged();
        _time.Advance(TimeSpan.FromMilliseconds(300));
        h.Service.OnDisplayChanged();
        _time.Advance(TimeSpan.FromMilliseconds(300));
        h.Service.OnDisplayChanged();            // three signals within a second: one debounce window
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));

        Assert.Equal(2, h.Applier.PerMonitorCalls.Count);
        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> reapply = h.Applier.PerMonitorCalls[1];
        Assert.Equal(3, reapply.Count);
        Assert.Contains("AlphornBavaria_EN-US6200857270", reapply[0].AbsolutePath);   // the current image stays primary
        Assert.Contains(MiddleId, reapply[1].AbsolutePath);
        Assert.Contains(OldestId, reapply[2].AbsolutePath);
        Assert.Equal([NewestId, MiddleId, OldestId], h.Cache.Index.Applied);
        Assert.Equal(1, LogCount("reapply reason=display monitors=3 result=Applied"));
        Assert.Equal(3, LogCount("display changed"));
        Assert.Equal(requestsBefore, h.Http.Requests.Count);   // no fetch on a re-apply
        Assert.Equal(nextDueBefore, h.Service.NextDueUtc);       // no schedule change
        Assert.Equal(lastAppliedBefore, h.State.LastAppliedUtc); // a re-apply is not a rotation
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.Equal(0, LogCount("tick reason=Interval"));

        // Undock it again: the set differs from the last apply -> one more re-apply over two monitors.
        h.Applier.Monitors.Remove(ThirdMonitor);
        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));
        Assert.Equal(3, h.Applier.PerMonitorCalls.Count);
        Assert.Equal(2, h.Applier.PerMonitorCalls[2].Count);
        Assert.Equal(1, LogCount("reapply reason=display monitors=2 result=Applied"));
    }

    [Fact]
    public async Task OnDisplayChanged_UnchangedSet_Skips()
    {
        Harness h = await PerMonitorAppliedAsync();

        h.Service.OnDisplayChanged();            // a DPI / resolution change: same two device paths
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));

        Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(1, LogCount("reapply skipped reason=display cause=unchanged"));
        Assert.Equal(0, LogCount("reapply reason=display"));
    }

    [Fact]
    public async Task OnDisplayChanged_PerMonitor_ReapplyFails_SetNotCommitted_SameSetRetriesNextSignal()
    {
        Harness h = await PerMonitorAppliedAsync();

        h.Applier.Monitors.Add(ThirdMonitor);   // docked a third display...
        h.Applier.FailNext = true;              // ...while the shell is still reconfiguring: this re-apply fails
        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));

        Assert.Equal(2, h.Applier.PerMonitorCalls.Count);
        Assert.Equal(1, LogCount("reapply reason=display monitors=3 result=Failed"));
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);   // RunPerMonitor restored the previous Applied list

        // The same three monitors signal again (a DPI change, the shell settling): the failed layout is retried, not
        // skipped as unchanged — the attached set is committed only after a SUCCESSFUL apply (WR-04).
        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));

        Assert.Equal(3, h.Applier.PerMonitorCalls.Count);
        Assert.Equal(3, h.Applier.PerMonitorCalls[2].Count);
        Assert.Equal(0, LogCount("reapply skipped reason=display cause=unchanged"));
        Assert.Equal(1, LogCount("reapply reason=display monitors=3 result=Applied"));
        Assert.Equal([NewestId, MiddleId, OldestId], h.Cache.Index.Applied);

        // Now the set IS committed: one more signal with the same three monitors is the unchanged case.
        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));

        Assert.Equal(3, h.Applier.PerMonitorCalls.Count);
        Assert.Equal(1, LogCount("reapply skipped reason=display cause=unchanged"));
    }

    [Fact]
    public async Task OnDisplayChanged_SameMode_Skips()
    {
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(Newest(), new AppState(), cache, applier: TwoMonitorApplier());
        await StartAsync(h);
        Assert.Equal(1, h.Applier.Calls);

        h.Applier.Monitors.Add(ThirdMonitor);
        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));

        Assert.Equal(1, h.Applier.Calls);
        Assert.Empty(h.Applier.PerMonitorCalls);
        Assert.Equal(0, h.Applier.MonitorEnumerations);
        Assert.Equal(1, LogCount("reapply skipped reason=display cause=same-mode"));
    }

    [Fact]
    public async Task OnDisplayChanged_WhileTickRunning_LogsBusy_ThenDeferredReapplyRunsAfterTickReleasesGate()
    {
        // WR-05: a display re-apply that meets a held gate is not dropped — the tick's finally runs it after the
        // release. Here the tick's own apply (monitors enumerated at apply time) already covers the attached set, so
        // the deferred re-apply is the unchanged case; the point is that it RAN.
        var dispatcher = new BlockingUiDispatcher();
        Harness h = Build(PerMonitor(), new AppState(), applier: TwoMonitorApplier(), dispatcher: dispatcher);
        Task<TickResult> tick = h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(h.Service.IsTickRunning);

        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.StateChanged += () =>
        {
            if (!h.Service.IsTickRunning && LogCount("reapply skipped reason=display cause=unchanged") == 1)
            {
                deferred.TrySetResult();
            }
        };

        h.Service.OnDisplayChanged();
        _time.Advance(TimeSpan.FromSeconds(3));   // the debounce fires while the tick still holds the gate

        Assert.Equal(1, LogCount("reapply skipped reason=display cause=busy"));
        Assert.Equal(0, LogCount("reapply reason=display"));
        Assert.Equal(0, LogCount("reapply skipped reason=display cause=unchanged"));

        dispatcher.Release();
        Assert.Equal(TickResult.Applied, await tick.WaitAsync(TimeSpan.FromSeconds(10)));
        await deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(h.Applier.PerMonitorCalls);   // the tick's own apply covered both monitors
        Assert.Equal(1, LogCount("reapply skipped reason=display cause=unchanged"));   // the deferred re-apply ran
        Assert.Equal(0, LogCount("reapply reason=display"));
        Assert.False(h.Service.IsTickRunning);

        // The flag was consumed: an unchanged signal later is one more unchanged skip, never a second deferred run.
        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));
        Assert.Equal(2, LogCount("reapply skipped reason=display cause=unchanged"));
        Assert.Single(h.Applier.PerMonitorCalls);
    }

    [Fact]
    public async Task OnDisplayChanged_DockWhileTickRunning_TickApplyFails_DeferredReapplyCoversNewMonitorAfterRelease()
    {
        // WR-05: dock a third display while a Next tick holds the gate at its apply, and let that apply fail (the
        // shell is still reconfiguring). Before the fix the busy display signal was dropped and the new monitor kept
        // whatever the shell assigned until the next display signal or applying tick; now the tick's release runs
        // the deferred display re-apply, which plans over all three monitors.
        var dispatcher = new BlockingUiDispatcher();
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17", 60), Cached(OldestId, "2026-09-14", 0)]);
        Harness h = Build(PerMonitor(), new AppState(), cache, RoutesFor(NewestId, MiddleId, OldestId), applier: TwoMonitorApplier(), dispatcher: dispatcher);   // CACHE-06: catalog pinned to the seeded cache

        Task<TickResult> startup = h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        dispatcher.Release();
        Assert.Equal(TickResult.Applied, await startup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);
        dispatcher.Rearm();

        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.StateChanged += () =>
        {
            // The tick's own raise sees two per-monitor calls; only the deferred re-apply's post-release raise matches.
            if (h.Applier.PerMonitorCalls.Count == 3 && !h.Service.IsTickRunning)
            {
                deferred.TrySetResult();
            }
        };

        Task<TickResult> next = h.Service.RunTickAsync(TickReason.Next, CancellationToken.None);
        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(10));   // parked at its per-monitor apply
        Assert.True(h.Service.IsTickRunning);

        h.Applier.Monitors.Add(ThirdMonitor);   // docked during the tick...
        h.Service.OnDisplayChanged();
        _time.Advance(TimeSpan.FromSeconds(3));   // ...and the debounce fires while the tick still holds the gate

        Assert.Equal(1, LogCount("reapply skipped reason=display cause=busy"));
        Assert.Equal(0, LogCount("reapply reason=display"));

        h.Applier.FailNext = true;   // the tick's apply fails: the attached set stays uncommitted (WR-04)
        dispatcher.Release();
        Assert.Equal(TickResult.ApplyFailed, await next.WaitAsync(TimeSpan.FromSeconds(10)));
        await deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, h.Applier.PerMonitorCalls.Count);   // startup, the failed Next, the deferred display re-apply
        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> reapply = h.Applier.PerMonitorCalls[2];
        Assert.Equal(3, reapply.Count);
        Assert.Contains("AlphornBavaria_EN-US6200857270", reapply[0].AbsolutePath);   // the current image stays primary
        Assert.Contains(MiddleId, reapply[1].AbsolutePath);
        Assert.Contains(OldestId, reapply[2].AbsolutePath);
        Assert.Equal([NewestId, MiddleId, OldestId], h.Cache.Index.Applied);
        Assert.Equal(1, LogCount("reapply reason=display monitors=3 result=Applied"));
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.False(h.Service.IsTickRunning);
        string log = LogText();
        int tickDone = log.IndexOf("tick done reason=Next", StringComparison.Ordinal);
        int reapplied = log.IndexOf("reapply reason=display monitors=3", StringComparison.Ordinal);
        Assert.True(tickDone >= 0 && reapplied > tickDone, "the deferred display re-apply must run after the tick ended");

        // Committed by the deferred re-apply: the same three monitors signalling again is the unchanged case.
        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));
        Assert.Equal(3, h.Applier.PerMonitorCalls.Count);
        Assert.Equal(1, LogCount("reapply skipped reason=display cause=unchanged"));
    }

    [Fact]
    public async Task OnDisplayChanged_BeforeDebounce_DoesNothing()
    {
        Harness h = await PerMonitorAppliedAsync();
        h.Applier.Monitors.Add(ThirdMonitor);

        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(2));

        Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(0, LogCount("reapply"));

        await AdvanceAsync(h, TimeSpan.FromSeconds(1));   // the 3 s window closes
        Assert.Equal(2, h.Applier.PerMonitorCalls.Count);
        Assert.Equal(1, LogCount("reapply reason=display monitors=3 result=Applied"));
    }

    [Fact]
    public async Task OnDisplayChanged_NothingAppliedYet_SkipsNoCurrent()
    {
        Harness h = Build(PerMonitor(), new AppState(), fake: Offline(), applier: TwoMonitorApplier());
        await StartAsync(h);
        Assert.Null(h.State.CurrentImageId);

        h.Service.OnDisplayChanged();
        await AdvanceAsync(h, TimeSpan.FromSeconds(3));

        Assert.Equal(1, LogCount("reapply skipped reason=display cause=no-current"));
        Assert.Equal(0, h.Applier.Calls);
    }

    [Fact]
    public async Task ApplySettings_MonitorModeToPerMonitor_ReappliesImmediately()
    {
        Settings settings = Newest();
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(settings, new AppState(), cache, RoutesFor(NewestId, MiddleId), applier: TwoMonitorApplier());   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);
        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal([NewestId], h.Cache.Index.Applied);
        int requestsBefore = h.Http.Requests.Count;

        settings.MonitorMode = Settings.PerMonitorMode;
        h.Service.ApplySettings();
        await h.Service.WaitForIdleAsync();

        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> call = Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(2, call.Count);
        Assert.Contains("AlphornBavaria_EN-US6200857270", call[0].AbsolutePath);
        Assert.Contains(MiddleId, call[1].AbsolutePath);
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);
        Assert.Equal(NewestId, h.State.CurrentImageId);
        Assert.Equal(requestsBefore, h.Http.Requests.Count);
        string log = LogText();
        Assert.Contains("reapply reason=settings monitors=2 result=Applied", log);
        Assert.Contains("settings applied interval=30 mode=newest resolution=UHD market=en-US monitors=perMonitor", log);
    }

    [Fact]
    public async Task ApplySettings_MonitorModeBackToSame_ReappliesSingle()
    {
        Settings settings = PerMonitor();
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(settings, new AppState(), cache, applier: TwoMonitorApplier());
        await StartAsync(h);
        Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(2, h.Cache.Index.Applied.Count);

        settings.MonitorMode = Settings.SameMonitorMode;
        h.Service.ApplySettings();
        await h.Service.WaitForIdleAsync();

        Assert.Equal(2, h.Applier.Calls);                       // +1 through Apply (NULL monitor = all)
        Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Contains("AlphornBavaria_EN-US6200857270", h.Applier.LastPath!);
        Assert.Equal([NewestId], h.Cache.Index.Applied);
        Assert.Equal(1, LogCount("reapply reason=settings monitors=2 result=Applied"));

        // Back again: the switch is honoured in both directions, every time.
        settings.MonitorMode = Settings.PerMonitorMode;
        h.Service.ApplySettings();
        await h.Service.WaitForIdleAsync();
        Assert.Equal(2, h.Applier.PerMonitorCalls.Count);
        Assert.Equal(2, LogCount("reapply reason=settings monitors=2 result=Applied"));
    }

    [Fact]
    public async Task ApplySettings_SameMonitorMode_NoReapply()
    {
        Settings settings = PerMonitor();
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(settings, new AppState(), cache, applier: TwoMonitorApplier());
        await StartAsync(h);

        settings.IntervalMinutes = 60;           // an interval-only change
        h.Service.ApplySettings();
        await h.Service.WaitForIdleAsync();

        Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(0, LogCount("reapply"));
        Assert.Contains("settings applied interval=60", LogText());
    }

    // ---- Plan 03-06: gap closure (03-VERIFICATION gaps 1 and 2; 03-REVIEW CR-01 / WR-01) ------------------

    [Fact]
    public async Task ApplySettings_MonitorModeSwitch_StateChangedAfterReapplyRelease_ObservesTickRunningFalse()
    {
        // CR-01 on the real WinFormsUiDispatcher: the forced re-apply holds the gate across its UI hop, so the raise
        // ApplySettings makes must precede the re-apply and the re-apply must raise again after its own release.
        var dispatcher = new BlockingUiDispatcher();
        Settings settings = Newest();
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(settings, new AppState(), cache, RoutesFor(NewestId, MiddleId), applier: TwoMonitorApplier(), dispatcher: dispatcher);   // CACHE-06: catalog pinned to the seeded cache

        Task<TickResult> startup = h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        dispatcher.Release();
        Assert.Equal(TickResult.Applied, await startup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal([NewestId], h.Cache.Index.Applied);
        dispatcher.Rearm();

        var observed = new List<bool>();
        bool released = false;
        var afterRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.StateChanged += () =>
        {
            bool running = h.Service.Snapshot().TickRunning;
            lock (observed)
            {
                observed.Add(running);
            }

            if (Volatile.Read(ref released))
            {
                afterRelease.TrySetResult(running);
            }
        };

        settings.MonitorMode = Settings.PerMonitorMode;
        h.Service.ApplySettings();

        lock (observed)
        {
            Assert.Equal([false], observed);   // the settings raise happened before the forced re-apply took the gate
        }

        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(10));   // the re-apply is parked at its UI hop
        Assert.True(h.Service.IsTickRunning);
        Assert.Empty(h.Applier.PerMonitorCalls);

        Volatile.Write(ref released, true);
        dispatcher.Release();
        bool last = await afterRelease.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(last);
        Assert.False(h.Service.IsTickRunning);
        lock (observed)
        {
            Assert.Equal([false, false], observed);
        }

        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> call = Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(2, call.Count);
        Assert.Contains("AlphornBavaria_EN-US6200857270", call[0].AbsolutePath);
        Assert.Contains(MiddleId, call[1].AbsolutePath);
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);
        string log = LogText();
        Assert.Contains("reapply reason=settings monitors=2 result=Applied", log);
        Assert.Contains("settings applied interval=30 mode=newest resolution=UHD market=en-US monitors=perMonitor", log);
    }

    [Fact]
    public async Task ApplySettings_MonitorModeSwitch_WhileTickRunning_LogsBusy_ThenReappliesAfterTickReleasesGate()
    {
        // WR-01 busy case: the switch lands while a tick holds the gate (Next click, download in flight). It is not
        // lost: the tick's finally re-checks the pending mode after its release and runs the forced re-apply then.
        var dispatcher = new BlockingUiDispatcher();
        Settings settings = Newest();
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(settings, new AppState(), cache, RoutesFor(NewestId, MiddleId), applier: TwoMonitorApplier(), dispatcher: dispatcher);   // CACHE-06: catalog pinned to the seeded cache

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.StateChanged += () =>
        {
            // The tick's own raise sees zero per-monitor calls; only the follow-up re-apply's post-release raise matches.
            if (h.Applier.PerMonitorCalls.Count == 1 && !h.Service.IsTickRunning)
            {
                done.TrySetResult();
            }
        };

        Task<TickResult> tick = h.Service.RunTickAsync(TickReason.Startup, CancellationToken.None);
        await dispatcher.Entered.WaitAsync(TimeSpan.FromSeconds(10));   // parked at its single-mode apply, branch already chosen

        settings.MonitorMode = Settings.PerMonitorMode;
        h.Service.ApplySettings();

        Assert.Equal(1, LogCount("reapply skipped reason=settings cause=busy"));
        Assert.Empty(h.Applier.PerMonitorCalls);
        Assert.Equal(0, LogCount("reapply reason=settings"));

        dispatcher.Release();
        Assert.Equal(TickResult.Applied, await tick.WaitAsync(TimeSpan.FromSeconds(10)));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> call = Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(2, call.Count);
        Assert.Contains("AlphornBavaria_EN-US6200857270", call[0].AbsolutePath);
        Assert.Contains(MiddleId, call[1].AbsolutePath);
        Assert.Equal(2, h.Applier.Calls);   // the tick's single apply plus the per-monitor follow-up
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);
        Assert.Equal(1, LogCount("reapply reason=settings monitors=2 result=Applied"));
        Assert.False(h.Service.IsTickRunning);
        string log = LogText();
        int tickDone = log.IndexOf("tick done reason=Startup", StringComparison.Ordinal);
        int reapplied = log.IndexOf("reapply reason=settings monitors=2", StringComparison.Ordinal);
        Assert.True(tickDone >= 0 && reapplied > tickDone, "the follow-up re-apply must run after the tick ended");

        // Committed by the follow-up: an unchanged ApplySettings re-runs nothing.
        h.Service.ApplySettings();
        await h.Service.WaitForIdleAsync();
        Assert.Equal(1, LogCount("reapply reason=settings monitors=2"));
    }

    [Fact]
    public async Task ApplySettings_MonitorModeSwitch_ReapplyFails_ModeNotCommitted_NextApplySettingsRetries()
    {
        // WR-01 failed case: a COM failure leaves the desktop in the old mode, so the mode must stay uncommitted and
        // the next ApplySettings must retry (and the failed re-apply still raises after its release).
        Settings settings = Newest();
        ImageCache cache = SeedCache([Cached(MiddleId, "2026-09-17")]);
        Harness h = Build(settings, new AppState(), cache, RoutesFor(NewestId, MiddleId), applier: TwoMonitorApplier());   // CACHE-06: catalog pinned to the seeded cache
        await StartAsync(h);
        int raised = 0;
        int runningWhenRaised = 0;
        h.Service.StateChanged += () =>
        {
            Interlocked.Increment(ref raised);
            if (h.Service.Snapshot().TickRunning)
            {
                Interlocked.Increment(ref runningWhenRaised);
            }
        };

        h.Applier.FailNext = true;
        settings.MonitorMode = Settings.PerMonitorMode;
        h.Service.ApplySettings();
        await h.Service.WaitForIdleAsync();

        Assert.Equal(1, LogCount("reapply reason=settings monitors=2 result=Failed"));
        Assert.Equal([NewestId], h.Cache.Index.Applied);   // RunPerMonitor restored the previous Applied list
        Assert.Equal(2, raised);                            // the settings raise + the failed re-apply's post-release raise
        Assert.Equal(0, runningWhenRaised);

        h.Service.ApplySettings();                          // nothing else changed: the uncommitted switch is retried
        await h.Service.WaitForIdleAsync();

        Assert.Equal(1, LogCount("reapply reason=settings monitors=2 result=Applied"));
        Assert.Equal(2, h.Applier.PerMonitorCalls.Count);
        Assert.Equal([NewestId, MiddleId], h.Cache.Index.Applied);
        Assert.Equal(4, raised);
        Assert.Equal(0, runningWhenRaised);
    }

    [Fact]
    public async Task ApplySettings_MonitorModeSwitch_NoCurrent_NotCommitted_FirstApplyingTickUsesNewMode()
    {
        // WR-01 no-current case: a switch before anything was applied commits nothing; the first tick that applies
        // uses the live mode and commits it, after which ApplySettings has nothing to re-apply.
        var network = new Network { Online = false };
        Settings settings = Newest();
        Harness h = Build(settings, new AppState(), fake: Switchable(network), applier: TwoMonitorApplier());
        await StartAsync(h);                                // the Startup tick fails: nothing current
        Assert.Null(h.State.CurrentImageId);

        settings.MonitorMode = Settings.PerMonitorMode;
        h.Service.ApplySettings();
        await h.Service.WaitForIdleAsync();

        Assert.Equal(1, LogCount("reapply skipped reason=settings cause=no-current"));
        Assert.Equal(0, h.Applier.Calls);

        network.Online = true;
        await AdvanceAsync(h, TimeSpan.FromMinutes(5));     // the first Retry tick fetches and applies in the live per-monitor mode

        IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> call = Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(2, call.Count);
        Assert.Equal(call[0].AbsolutePath, call[1].AbsolutePath);   // one cached image: same on all
        Assert.Equal(NewestId, h.State.CurrentImageId);

        h.Service.ApplySettings();                          // the tick committed the mode: nothing to re-apply
        await h.Service.WaitForIdleAsync();

        Assert.Equal(0, LogCount("reapply reason=settings"));
        Assert.Equal(1, LogCount("reapply skipped reason=settings"));
    }

    [Fact]
    public async Task OnDisplayChanged_AfterDispose_IsIgnored()
    {
        Harness h = await PerMonitorAppliedAsync();
        h.Applier.Monitors.Add(ThirdMonitor);
        h.Service.Dispose();

        h.Service.OnDisplayChanged();
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Single(h.Applier.PerMonitorCalls);
        Assert.Equal(0, LogCount("display changed"));
        Assert.Equal(0, LogCount("reapply"));
    }

    // ---- Phase 4: CACHE-06 backfill ---------------------------------------------------------------------

    [Fact]
    public async Task Backfill_StartupTick_EmptyCache_DownloadsNewestPlusOneOlder_AfterApply()
    {
        Harness h = Build(Newest(), new AppState());

        await StartAsync(h);

        Assert.Equal(1, h.Applier.Calls);
        Assert.Equal(2, ImageDownloads(h));                 // the newest + exactly one backfill
        Assert.Equal(2, h.Cache.Index.Images.Count);
        string[] cachedIds = h.Cache.Index.Images.Select(i => i.Id).ToArray();
        Assert.Equal(2, cachedIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { NewestId, SecondId }, cachedIds);   // README.sample.md row 2 is the first backfill candidate
        Assert.Equal(NewestId, h.State.LastSeenNewestId);
        Assert.Equal(new[] { NewestId }, h.Cache.Index.Applied);   // the backfilled image is never the one applied

        string log = LogText();
        int applyAt = log.IndexOf("apply ok", StringComparison.Ordinal);
        int backfillAt = log.IndexOf("backfill id=", StringComparison.Ordinal);
        int doneAt = log.IndexOf("tick done reason=Startup", StringComparison.Ordinal);
        Assert.True(applyAt >= 0 && backfillAt > applyAt && doneAt > backfillAt, $"expected apply ok < backfill id= < tick done, got {applyAt}/{backfillAt}/{doneAt}");
        Assert.Equal(1, LogCount("backfill id="));
        string backfillLine = log.Split('\n').Single(l => l.Contains("backfill id=", StringComparison.Ordinal)).TrimEnd('\r');
        Assert.EndsWith("cached=2/10", backfillLine, StringComparison.Ordinal);
        Assert.Contains($"backfill id={SecondId} file=", backfillLine, StringComparison.Ordinal);
    }

    /// <summary>ROADMAP success criterion 6: ten successful ticks from an empty cache reach ten distinct catalog IDs, one backfill per tick, then the cap holds.</summary>
    [Fact]
    public async Task Backfill_TenTicks_EmptyCache_ReachesTenDistinctIds_ThenSkipsFull()
    {
        IReadOnlyList<(string Date, string Id)> rows = ReadmeRows();
        Assert.Equal(SecondId, rows[1].Id);   // the constants name README.sample.md rows 2 and 3
        Assert.Equal(ThirdId, rows[2].Id);

        Harness h = Build(Newest(), new AppState());
        await StartAsync(h);
        Assert.Equal(2, h.Cache.Index.Images.Count);

        int intervalTicks = 0;
        while (h.Cache.Index.Images.Count < ImageCache.MaxImages && intervalTicks < 20)
        {
            await AdvanceAsync(h, Interval);
            intervalTicks++;
            Assert.Equal(2 + intervalTicks, h.Cache.Index.Images.Count);   // exactly one backfill per successful tick
        }

        Assert.Equal(8, intervalTicks);                                    // Startup + 8 Interval ticks = 10 images
        Assert.Equal(10, h.Cache.Index.Images.Count);
        string[] ids = h.Cache.Index.Images.Select(i => i.Id).ToArray();
        Assert.Equal(10, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(rows.Take(10).Select(r => r.Id), ids);                // the newest plus the nine next older days
        Assert.Equal(10, ImageDownloads(h));                               // 1 newest + 9 backfills
        Assert.Equal(9, LogCount("backfill id="));
        Assert.Equal(0, LogCount("backfill skipped"));
        Assert.Equal(1, h.Applier.Calls);                                  // only the newest was ever applied

        await AdvanceAsync(h, Interval);                                   // one more: the cap holds

        Assert.Equal(1, LogCount("backfill skipped cause=full"));
        Assert.Equal(10, ImageDownloads(h));
        Assert.Equal(10, h.Cache.Index.Images.Count);
        Assert.Equal(9, LogCount("backfill id="));
    }

    [Fact]
    public async Task Backfill_CacheAtCap_LogsFull_NoDownload()
    {
        IReadOnlyList<(string Date, string Id)> rows = ReadmeRows();
        CachedImage[] ten = rows.Take(ImageCache.MaxImages).Select((r, i) => Cached(r.Id, r.Date, 100 - i * 10)).ToArray();
        ImageCache cache = SeedCache(ten, applied: NewestId);
        var state = new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0.AddMinutes(20) };
        Harness h = Build(Newest(), state, cache);

        await StartAsync(h);

        Assert.Equal(0, ImageDownloads(h));
        Assert.Equal(0, h.Applier.Calls);
        Assert.Equal(1, LogCount("backfill skipped cause=full"));
        Assert.Equal(0, LogCount("backfill id="));
        Assert.Equal(10, h.Cache.Index.Images.Count);
        Assert.Contains("tick done reason=Startup result=NoOp", LogText());
    }

    [Fact]
    public async Task Backfill_AllCatalogRowsCached_LogsNoCandidate()
    {
        Harness h = Build(Newest(), new AppState(), fake: Routes(ReadmeWithRows(2)));

        await StartAsync(h);
        Assert.Equal(2, h.Cache.Index.Images.Count);
        Assert.Equal(2, ImageDownloads(h));
        Assert.Equal(1, LogCount("backfill id="));

        await AdvanceAsync(h, Interval);

        Assert.Equal(1, LogCount("backfill skipped cause=no-candidate"));
        Assert.Equal(2, ImageDownloads(h));
        Assert.Equal(2, h.Cache.Index.Images.Count);
        Assert.Equal(1, LogCount("backfill id="));
        Assert.Contains("tick done reason=Interval result=NoOp decision=NoOp why=unchanged", LogText());
    }

    [Fact]
    public async Task Backfill_OfflineTick_DownloadsNothing_LeavesLadderAlone()
    {
        Harness h = Build(Newest(), new AppState(), fake: Offline());

        await StartAsync(h);

        Assert.Equal(0, ImageDownloads(h));
        Assert.DoesNotContain("backfill", LogText(), StringComparison.Ordinal);
        Assert.Equal(0, h.Applier.Calls);
        Assert.Empty(h.Cache.Index.Images);
        Assert.Equal(1, h.Service.FailureStage);                           // the pre-existing D-13 ladder, untouched
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Service.RetryDueUtc);
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        Assert.Contains("tick done reason=Startup result=FetchFailed", LogText());
        AssertStateFileHasNoRetry();
    }

    [Fact]
    public async Task Backfill_DeadCatalogId_IsSkippedForTheProcess()
    {
        Harness h = Build(Newest(), new AppState(), fake: RoutesWithDeadImage(SecondId));
        int DeadRequests() => h.Http.Requests.Count(r => r.Uri.Query.Contains(SecondId, StringComparison.Ordinal));

        await StartAsync(h);

        Assert.Equal(1, LogCount($"backfill failed id={SecondId}"));
        Assert.Equal(0, LogCount("backfill id="));
        Assert.Equal(new[] { NewestId }, h.Cache.Index.Images.Select(i => i.Id));   // only the newest was cached
        Assert.Equal(1, DeadRequests());
        Assert.Equal(2, ImageDownloads(h));                                // newest + the rejected attempt

        await AdvanceAsync(h, Interval);                                   // the dead ID is not asked for again

        Assert.Equal(1, DeadRequests());
        Assert.Equal(1, LogCount($"backfill id={ThirdId} "));
        Assert.Equal(new[] { NewestId, ThirdId }, h.Cache.Index.Images.Select(i => i.Id));
        Assert.Equal(3, ImageDownloads(h));
        Assert.Equal(1, LogCount("backfill failed id="));
    }

    [Fact]
    public async Task Backfill_Failure_NeverTouchesTickResultOrLadder()
    {
        Harness h = Build(Newest(), new AppState(), fake: RoutesWithDeadImage(SecondId));

        await StartAsync(h);

        Assert.Equal(1, LogCount($"backfill failed id={SecondId}"));
        Assert.Contains("tick done reason=Startup result=Applied decision=ApplyNew why=new", LogText());
        Assert.DoesNotContain("tick failed", LogText(), StringComparison.Ordinal);
        Assert.DoesNotContain("retry scheduled", LogText(), StringComparison.Ordinal);
        Assert.Equal(0, h.Service.FailureStage);
        Assert.Null(h.Service.RetryDueUtc);
        Assert.Equal(T0 + Interval, h.Service.NextDueUtc);
        Assert.Equal(1, h.Applier.Calls);

        // The saved state is the no-backfill contract of StartupTick_NewCatalogId_AppliesWithinOneTick_SetsLastSeenAndNextDue.
        AppState? saved = SavedState();
        Assert.NotNull(saved);
        Assert.Equal(NewestId, saved!.LastSeenNewestId);
        Assert.Equal(NewestId, saved.CurrentImageId);
        Assert.Equal(T0 + Interval, saved.NextDueUtc);
        Assert.Equal(T0, saved.LastCheckUtc);
        Assert.Equal(T0, saved.LastAppliedUtc);
        AssertStateFileHasNoRetry();
    }

    [Fact]
    public async Task Backfill_UsesTickResolution()
    {
        Settings settings = Newest();
        settings.Resolution = "1920x1080";
        Harness h = Build(settings, new AppState());

        await StartAsync(h);

        List<RecordedRequest> images = ImageRequests(h);
        Assert.Equal(2, images.Count);
        Assert.All(images, image => Assert.Contains("w=1920&h=1080", image.Uri.Query, StringComparison.Ordinal));
        CachedImage backfilled = Assert.Single(h.Cache.Index.Images, i => string.Equals(i.Id, SecondId, StringComparison.Ordinal));
        Assert.Equal("1920x1080", backfilled.Resolution);
        Assert.EndsWith(".1920x1080.jpg", backfilled.File, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_dir, backfilled.File)));
        Assert.Contains($"backfill id={SecondId} file={backfilled.File} cached=2/10", LogText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// IN-03: five days cached at UHD, the user switches to 1920x1080. The decider keeps the UHD current image (one
    /// candidate per ID, any resolution), so the only download is the backfill — and it must not re-fetch days 1-5 as
    /// 1080 twins: it prefers the first catalog row absent at every resolution, so the cap fills with new days.
    /// </summary>
    [Fact]
    public async Task Backfill_AfterResolutionChange_PrefersIdsAbsentAtEveryResolution()
    {
        IReadOnlyList<(string Date, string Id)> rows = ReadmeRows();
        CachedImage[] fiveUhd = rows.Take(5).Select((r, i) => Cached(r.Id, r.Date, 100 - i * 10)).ToArray();
        ImageCache cache = SeedCache(fiveUhd, applied: NewestId);
        Settings settings = Newest();
        settings.Resolution = "1920x1080";
        Harness h = Build(settings, new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0.AddMinutes(20) }, cache);

        await StartAsync(h);

        Assert.Contains("tick done reason=Startup result=NoOp decision=NoOp why=not-due", LogText());
        Assert.Equal(1, ImageDownloads(h));                                // the backfill only
        Assert.Equal(6, h.Cache.Index.Images.Count);                       // 5 UHD + the backfill
        string sixthDay = rows[5].Id;
        CachedImage backfilled = Assert.Single(h.Cache.Index.Images, i => string.Equals(i.Id, sixthDay, StringComparison.Ordinal));
        Assert.Equal("1920x1080", backfilled.Resolution);
        Assert.EndsWith(".1920x1080.jpg", backfilled.File, StringComparison.Ordinal);
        Assert.Contains($"backfill id={sixthDay} file={backfilled.File} cached=6/10", LogText(), StringComparison.Ordinal);
        foreach ((string _, string id) in rows.Take(5))
        {
            CachedImage only = Assert.Single(h.Cache.Index.Images, i => string.Equals(i.Id, id, StringComparison.Ordinal));   // no 1080 twin
            Assert.Equal("UHD", only.Resolution);
        }
    }

    /// <summary>IN-03 fallback: once every catalog row is held at some resolution, the original "not cached at this resolution" rule resumes, newest first.</summary>
    [Fact]
    public async Task Backfill_AfterResolutionChange_FallsBackToCurrentResolutionRule_WhenEveryRowIsCachedSomewhere()
    {
        ImageCache cache = SeedCache([Cached(NewestId, "2026-09-20", 10), Cached(SecondId, "2026-09-19", 0)], applied: NewestId);
        Settings settings = Newest();
        settings.Resolution = "1920x1080";
        Harness h = Build(settings, new AppState { CurrentImageId = NewestId, LastSeenNewestId = NewestId, NextDueUtc = T0.AddMinutes(20) }, cache, fake: Routes(ReadmeWithRows(2)));

        await StartAsync(h);                                               // both rows are held at UHD: pass 1 finds nothing

        Assert.Equal(1, ImageDownloads(h));
        Assert.Equal(3, h.Cache.Index.Images.Count);
        CachedImage newestTwin = Assert.Single(h.Cache.Index.Images, i => string.Equals(i.Id, NewestId, StringComparison.Ordinal) && string.Equals(i.Resolution, "1920x1080", StringComparison.Ordinal));
        Assert.EndsWith(".1920x1080.jpg", newestTwin.File, StringComparison.Ordinal);
        Assert.Contains($"backfill id={NewestId} file={newestTwin.File} cached=3/10", LogText(), StringComparison.Ordinal);

        await AdvanceAsync(h, Interval);                                   // pass 2 again: the day-2 twin

        Assert.Equal(2, ImageDownloads(h));
        Assert.Equal(4, h.Cache.Index.Images.Count);
        Assert.Single(h.Cache.Index.Images, i => string.Equals(i.Id, SecondId, StringComparison.Ordinal) && string.Equals(i.Resolution, "1920x1080", StringComparison.Ordinal));
        Assert.Equal(1, LogCount($"backfill id={SecondId} "));

        await AdvanceAsync(h, Interval);                                   // now every row is cached at 1080 too

        Assert.Equal(1, LogCount("backfill skipped cause=no-candidate"));
        Assert.Equal(2, ImageDownloads(h));
    }

    [Fact]
    public async Task Backfill_LogOrder_ApplyThenBackfillThenTickDone()
    {
        Harness h = Build(Newest(), new AppState());
        await StartAsync(h);
        Assert.Equal(1, LogCount("apply ok"));

        await AdvanceAsync(h, Interval);                                   // unchanged catalog: NoOp, still backfills

        string log = LogText();
        int tickAt = log.IndexOf("tick reason=Interval", StringComparison.Ordinal);
        Assert.True(tickAt >= 0, "no Interval tick logged");
        string interval = log[tickAt..];
        Assert.DoesNotContain("apply ok", interval, StringComparison.Ordinal);
        int backfillAt = interval.IndexOf("backfill id=", StringComparison.Ordinal);
        int doneAt = interval.IndexOf("tick done reason=Interval", StringComparison.Ordinal);
        Assert.True(backfillAt > 0 && doneAt > backfillAt, $"expected tick reason=Interval < backfill id= < tick done reason=Interval, got 0/{backfillAt}/{doneAt}");
        Assert.Contains("tick done reason=Interval result=NoOp decision=NoOp why=unchanged", interval, StringComparison.Ordinal);
        Assert.Equal(2, LogCount("backfill id="));
        Assert.Equal(3, h.Cache.Index.Images.Count);
    }
}
