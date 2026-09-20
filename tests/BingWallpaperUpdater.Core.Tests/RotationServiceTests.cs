using System.Text.RegularExpressions;
using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;
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

    private static CachedImage Cached(string id, string date, int downloadedMinutesAfterT0 = 0, string resolution = "UHD", int width = 3840) => new()
    {
        Id = id,
        Market = "EN-US",
        Date = date,
        Resolution = resolution,
        Width = width,
        Height = width * 9 / 16,
        File = $"{date}_{id}.jpg",
        Bytes = 16,
        DownloadedUtc = T0.AddMinutes(downloadedMinutesAfterT0),
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

        var cache = new ImageCache(_dir, _indexPath);
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

    private Harness Build(Settings settings, AppState state, ImageCache? cache = null, FakeHttpHandler? fake = null, FakeApplier? applier = null, Core.Ports.IUiDispatcher? dispatcher = null)
    {
        fake ??= Routes();
        applier ??= new FakeApplier();
        cache ??= SeedCache([]);
        var http = new HttpGateway(fake, retry: NoDelay()) { DownloadRoot = _dir };
        _owned.Add(http);
        var catalog = new CatalogService(http, state, _catalogPath);
        var service = new RotationService(settings, state, _statePath, catalog, cache, http, applier, dispatcher ?? new InlineUiDispatcher(), _time, new Random(20260920));
        _owned.Add(service);
        return new Harness(service, state, applier, fake, cache);
    }

    private static Settings Newest() => new() { IntervalMinutes = 30, Mode = Settings.DefaultMode };

    private static Settings RandomMode() => new() { IntervalMinutes = 30, Mode = Settings.RandomMode };

    private AppState? SavedState() => AtomicJsonFile.Load(_statePath, CoreJsonContext.Default.AppState);

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
        Harness h = Build(RandomMode(), state, cache);
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
        Harness h = Build(Newest(), state, cache);
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
        Harness h = Build(Newest(), state, cache);
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
        Harness h = Build(RandomMode(), state, cache);
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
        Harness h = Build(Newest(), state, cache);
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
}
