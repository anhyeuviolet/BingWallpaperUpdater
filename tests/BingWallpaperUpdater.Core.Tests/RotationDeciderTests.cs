using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Rotation;
using BingWallpaperUpdater.Core.Scheduling;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// The decision table (RESEARCH Pattern 3; D-03, D-04, D-05, D-11, D-13) row by row, plus the ordering, step-older
/// wrap, random-excluding-current and candidate-selection helpers. Pure: no LogSink, no temp dir.
/// </summary>
public sealed class RotationDeciderTests
{
    private const string NewestId = "OHR.AlphornBavaria_EN-US6200857270";
    private const string MiddleId = "OHR.IcyCubs_EN-US5222104616";
    private const string OldestId = "OHR.MisurinaPeak_EN-US4897144498";
    private const string FreshId = "OHR.ParisSunset_EN-US6532307523";

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Random Seeded = new(20260920);

    private static CachedImage Cached(string id, string? date, int downloadedMinutesAfterT0 = 0, string resolution = "UHD", int width = 3840) => new()
    {
        Id = id,
        Market = "EN-US",
        Date = date,
        Resolution = resolution,
        Width = width,
        Height = width * 9 / 16,
        File = $"{date ?? "nodate"}_{id}_{resolution}.jpg",
        Bytes = 16,
        DownloadedUtc = T0.AddMinutes(downloadedMinutesAfterT0),
    };

    private static CatalogEntry Entry(string id)
    {
        Assert.True(ImageId.TryParse(id, out ImageId parsed));
        return new CatalogEntry(parsed, "2026-09-20", null, null, null, null, CatalogSources.GitHub);
    }

    /// <summary>Three cached images, newest first, matching README.sample.md dates.</summary>
    private static IReadOnlyList<CachedImage> Three() =>
    [
        Cached(NewestId, "2026-09-20", 120),
        Cached(MiddleId, "2026-09-17", 60),
        Cached(OldestId, "2026-09-14", 0),
    ];

    private static IReadOnlyList<CachedImage> Two() => [Cached(NewestId, "2026-09-20", 60), Cached(MiddleId, "2026-09-17", 0)];

    private static IReadOnlyList<CachedImage> One() => [Cached(NewestId, "2026-09-20")];

    private static AppState Current(string? currentId, string? lastSeen) => new() { CurrentImageId = currentId, LastSeenNewestId = lastSeen };

    private static RotationDecision Decide(TickReason reason, bool random, CatalogEntry? newest, AppState state, IReadOnlyList<CachedImage> candidates, bool intervalDue = true) =>
        RotationDecider.Decide(reason, random, newest, state, candidates, intervalDue, Seeded);

    // ---- rows -----------------------------------------------------------------------------------------

    [Fact]
    public void NewestMode_UnchangedCatalog_NoOp()
    {
        RotationDecision d = Decide(TickReason.Interval, false, Entry(NewestId), Current(NewestId, NewestId), Three());

        Assert.Equal(DecisionKind.NoOp, d.Kind);
        Assert.Equal("unchanged", d.Why);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewestMode_NewId_ApplyNew(bool randomMode)
    {
        RotationDecision d = Decide(TickReason.Interval, randomMode, Entry(FreshId), Current(NewestId, NewestId), Three());

        Assert.Equal(DecisionKind.ApplyNew, d.Kind);
        Assert.Equal(FreshId, d.Entry!.Id.Value);
        Assert.Null(d.Target);
        Assert.Equal("new", d.Why);
    }

    // ---- WR-01: a source switch must not present an older, already-downloaded ID as "new" ------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceSwitch_OlderNewestDownloadedBeforeLastSeen_NotNew(bool randomMode)
    {
        // The README still lists the middle image while HPImageArchive already served (and the app applied) the
        // newest: the older ID is cached and was downloaded before the last-seen one, so it is not new.
        RotationDecision d = Decide(TickReason.Interval, randomMode, Entry(MiddleId), Current(NewestId, NewestId), Three());

        Assert.NotEqual(DecisionKind.ApplyNew, d.Kind);
        if (randomMode)
        {
            Assert.Equal(DecisionKind.Random, d.Kind);   // the interval rotation still runs (R6)
        }
        else
        {
            Assert.Equal(DecisionKind.NoOp, d.Kind);
            Assert.Equal("unchanged", d.Why);
        }
    }

    [Fact]
    public void SourceSwitch_OlderNewest_AfterUserStepBack_NotNew()
    {
        // The user stepped back to the middle image and GitHub recovers with yesterday's ID: nothing to apply.
        RotationDecision d = Decide(TickReason.Interval, false, Entry(MiddleId), Current(MiddleId, NewestId), Three());

        Assert.Equal(DecisionKind.NoOp, d.Kind);
        Assert.Equal("unchanged", d.Why);
    }

    [Fact]
    public void CachedNewestDownloadedAfterLastSeen_StillApplyNew_OnRetry()
    {
        // The newest was downloaded but its apply failed or threw: it is cached and newer than the last-seen entry,
        // so the ladder Retry must still apply it (R2 before R4).
        RotationDecision d = Decide(TickReason.Retry, false, Entry(NewestId), Current(MiddleId, MiddleId), Three());

        Assert.Equal(DecisionKind.ApplyNew, d.Kind);
        Assert.Equal(NewestId, d.Entry!.Id.Value);
        Assert.Equal("new", d.Why);
    }

    [Fact]
    public void CachedNewest_LastSeenNotCached_ApplyNew()
    {
        // A last-seen entry that is no longer cached cannot vouch for the catalog newest (smoke S8's fake ID).
        RotationDecision d = Decide(TickReason.Startup, false, Entry(NewestId), Current(NewestId, FreshId), Three());

        Assert.Equal(DecisionKind.ApplyNew, d.Kind);
        Assert.Equal("new", d.Why);
    }

    [Fact]
    public void UncachedNewest_AlwaysApplyNew()
    {
        RotationDecision d = Decide(TickReason.Interval, false, Entry(FreshId), Current(MiddleId, NewestId), Three());

        Assert.Equal(DecisionKind.ApplyNew, d.Kind);
        Assert.Equal(FreshId, d.Entry!.Id.Value);
        Assert.Equal("new", d.Why);
    }

    [Fact]
    public void Phase1Upgrade_LastSeenNull_CurrentEqualsNewest_SeedLastSeen()
    {
        RotationDecision d = Decide(TickReason.Startup, false, Entry(NewestId), Current(NewestId, lastSeen: null), Three());

        Assert.Equal(DecisionKind.SeedLastSeen, d.Kind);
        Assert.Equal(NewestId, d.Entry!.Id.Value);
        Assert.Equal("seed", d.Why);
    }

    [Fact]
    public void Phase1Upgrade_LastSeenNull_CurrentDiffersFromNewest_ApplyNew()
    {
        RotationDecision d = Decide(TickReason.Startup, false, Entry(FreshId), Current(NewestId, lastSeen: null), Three());

        Assert.Equal(DecisionKind.ApplyNew, d.Kind);
        Assert.Equal("new", d.Why);
    }

    [Fact]
    public void MissingCurrent_WithCatalog_ApplyNew_WithoutCatalog_StepOlderNewest_EmptyCache_NoOp()
    {
        // current file gone (not among the candidates) but the catalog answered: re-apply the newest (why=missing-current)
        RotationDecision withCatalog = Decide(TickReason.Interval, false, Entry(NewestId), Current(FreshId, NewestId), Three());
        Assert.Equal(DecisionKind.ApplyNew, withCatalog.Kind);
        Assert.Equal(NewestId, withCatalog.Entry!.Id.Value);
        Assert.Equal("missing-current", withCatalog.Why);

        // offline: newest cached image instead
        RotationDecision offline = Decide(TickReason.Interval, false, null, Current(FreshId, NewestId), Three());
        Assert.Equal(DecisionKind.StepOlder, offline.Kind);
        Assert.Equal(NewestId, offline.Target!.Id);
        Assert.Equal("missing-current", offline.Why);

        // null current is "missing" too
        RotationDecision nullCurrent = Decide(TickReason.Startup, true, null, Current(null, null), Two());
        Assert.Equal(DecisionKind.StepOlder, nullCurrent.Kind);
        Assert.Equal(NewestId, nullCurrent.Target!.Id);

        // nothing anywhere: no throw, empty-cache (ROT-03 empty edge)
        RotationDecision empty = Decide(TickReason.Interval, true, null, Current(null, null), []);
        Assert.Equal(DecisionKind.NoOp, empty.Kind);
        Assert.Equal("empty-cache", empty.Why);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retry_AlwaysNoOp_EvenInRandomModeWithManyImages(bool randomMode)
    {
        RotationDecision d = Decide(TickReason.Retry, randomMode, null, Current(NewestId, NewestId), Three());

        Assert.Equal(DecisionKind.NoOp, d.Kind);
        Assert.Equal("retry", d.Why);
    }

    [Fact]
    public void Next_Newest_TwoOrMore_StepsToNextOlder_AndWrapsToNewest()
    {
        IReadOnlyList<CachedImage> three = Three();
        CatalogEntry unchanged = Entry(NewestId);

        RotationDecision first = Decide(TickReason.Next, false, unchanged, Current(NewestId, NewestId), three);
        Assert.Equal(DecisionKind.StepOlder, first.Kind);
        Assert.Equal(MiddleId, first.Target!.Id);
        Assert.Equal("next", first.Why);

        RotationDecision second = Decide(TickReason.Next, false, unchanged, Current(MiddleId, NewestId), three);
        Assert.Equal(DecisionKind.StepOlder, second.Kind);
        Assert.Equal(OldestId, second.Target!.Id);

        RotationDecision third = Decide(TickReason.Next, false, unchanged, Current(OldestId, NewestId), three);
        Assert.Equal(DecisionKind.StepOlder, third.Kind);
        Assert.Equal(NewestId, third.Target!.Id);   // wrap
    }

    [Fact]
    public void Next_Random_PicksOther()
    {
        var random = new Random(7);
        for (int i = 0; i < 50; i++)
        {
            RotationDecision d = RotationDecider.Decide(TickReason.Next, true, Entry(NewestId), Current(MiddleId, NewestId), Three(), true, random);
            Assert.Equal(DecisionKind.Random, d.Kind);
            Assert.NotEqual(MiddleId, d.Target!.Id);
            Assert.Equal("next", d.Why);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Next_SingleImage_NoOp_SingleImage(bool randomMode)
    {
        RotationDecision d = Decide(TickReason.Next, randomMode, Entry(NewestId), Current(NewestId, NewestId), One());

        Assert.Equal(DecisionKind.NoOp, d.Kind);
        Assert.Equal("single-image", d.Why);
    }

    [Fact]
    public void Interval_Random_TwoOrMore_Random()
    {
        RotationDecision d = Decide(TickReason.Interval, true, Entry(NewestId), Current(NewestId, NewestId), Two());

        Assert.Equal(DecisionKind.Random, d.Kind);
        Assert.Equal(MiddleId, d.Target!.Id);
        Assert.Equal("interval", d.Why);
    }

    [Fact]
    public void Startup_Random_NotDue_NoOp_NotDue()
    {
        RotationDecision d = Decide(TickReason.Startup, true, Entry(NewestId), Current(NewestId, NewestId), Two(), intervalDue: false);

        Assert.Equal(DecisionKind.NoOp, d.Kind);
        Assert.Equal("not-due", d.Why);
    }

    [Fact]
    public void Startup_Random_Due_Random()
    {
        RotationDecision d = Decide(TickReason.Startup, true, Entry(NewestId), Current(NewestId, NewestId), Two(), intervalDue: true);

        Assert.Equal(DecisionKind.Random, d.Kind);
        Assert.Equal(MiddleId, d.Target!.Id);
        Assert.Equal("interval", d.Why);
    }

    [Fact]
    public void Startup_Newest_NotDue_NoOp_NotDue()
    {
        RotationDecision d = Decide(TickReason.Startup, false, Entry(NewestId), Current(NewestId, NewestId), Three(), intervalDue: false);

        Assert.Equal(DecisionKind.NoOp, d.Kind);
        Assert.Equal("not-due", d.Why);
    }

    [Fact]
    public void Interval_Random_SingleImage_NoOp_Unchanged()
    {
        RotationDecision d = Decide(TickReason.Interval, true, Entry(NewestId), Current(NewestId, NewestId), One());

        Assert.Equal(DecisionKind.NoOp, d.Kind);
        Assert.Equal("unchanged", d.Why);   // ROT-03: random behaves as newest until two images are cached
    }

    [Fact]
    public void Interval_Random_Offline_TwoOrMore_StillRotates()
    {
        RotationDecision d = Decide(TickReason.Interval, true, null, Current(NewestId, NewestId), Two());

        Assert.Equal(DecisionKind.Random, d.Kind);   // D-14: offline random keeps rotating from the cache
        Assert.Equal(MiddleId, d.Target!.Id);
    }

    [Fact]
    public void Interval_Newest_Offline_NoOp_Unchanged()
    {
        RotationDecision d = Decide(TickReason.Interval, false, null, Current(NewestId, NewestId), Two());

        Assert.Equal(DecisionKind.NoOp, d.Kind);   // D-14: offline newest does nothing
        Assert.Equal("unchanged", d.Why);
    }

    [Fact]
    public void Decide_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => RotationDecider.Decide(TickReason.Interval, false, null, null!, Three(), true, Seeded));
        Assert.Throws<ArgumentNullException>(() => RotationDecider.Decide(TickReason.Interval, false, null, new AppState(), null!, true, Seeded));
        Assert.Throws<ArgumentNullException>(() => RotationDecider.Decide(TickReason.Interval, false, null, new AppState(), Three(), true, null!));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    [Fact]
    public void StepOlder_UnknownCurrent_ReturnsNewest()
    {
        IReadOnlyList<CachedImage> three = Three();

        Assert.Equal(NewestId, RotationDecider.StepOlder(three, "OHR.Evicted_EN-US1")!.Id);
        Assert.Equal(NewestId, RotationDecider.StepOlder(three, null)!.Id);
        Assert.Null(RotationDecider.StepOlder([], NewestId));
    }

    [Fact]
    public void StepOlder_WrapsFromOldestToNewest()
    {
        IReadOnlyList<CachedImage> three = Three();

        Assert.Equal(MiddleId, RotationDecider.StepOlder(three, NewestId)!.Id);
        Assert.Equal(OldestId, RotationDecider.StepOlder(three, MiddleId)!.Id);
        Assert.Equal(NewestId, RotationDecider.StepOlder(three, OldestId)!.Id);
    }

    [Fact]
    public void OrderNewestFirst_DateDescThenDownloadedDesc_NullDateLast()
    {
        CachedImage a = Cached("OHR.A_EN-US1", "2026-09-10", 0);
        CachedImage b = Cached("OHR.B_EN-US2", "2026-09-12", 5);
        CachedImage c = Cached("OHR.C_EN-US3", "2026-09-12", 9);   // same date as b, downloaded later -> before b
        CachedImage d = Cached("OHR.D_EN-US4", null, 99);           // null date sorts last regardless of download time

        IReadOnlyList<CachedImage> ordered = RotationDecider.OrderNewestFirst([a, d, b, c]);

        Assert.Equal(["OHR.C_EN-US3", "OHR.B_EN-US2", "OHR.A_EN-US1", "OHR.D_EN-US4"], ordered.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void RandomOther_ThreeImages_200Draws_NeverCurrent()
    {
        var random = new Random(7);
        IReadOnlyList<CachedImage> three = Three();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 200; i++)
        {
            CachedImage? pick = RotationDecider.RandomOther(three, MiddleId, random);
            Assert.NotNull(pick);
            Assert.NotEqual(MiddleId, pick!.Id);
            seen.Add(pick.Id);
        }

        Assert.Equal(2, seen.Count);   // both other images are actually reachable
    }

    [Fact]
    public void RandomOther_TwoImages_AlwaysTheOther()
    {
        var random = new Random(3);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(MiddleId, RotationDecider.RandomOther(Two(), NewestId, random)!.Id);
        }
    }

    [Fact]
    public void RandomOther_SingleImageOrOnlyCurrent_ReturnsNull()
    {
        Assert.Null(RotationDecider.RandomOther(One(), NewestId, Seeded));
        Assert.Null(RotationDecider.RandomOther([], null, Seeded));
    }

    [Fact]
    public void Candidates_PrefersPreferredResolutionThenWidth_DropsMissingFiles()
    {
        CachedImage newestUhd = Cached(NewestId, "2026-09-20", 10, "UHD", 3840);
        CachedImage newestFhd = Cached(NewestId, "2026-09-20", 11, "1920x1080", 1920);
        CachedImage middleWide = Cached(MiddleId, "2026-09-17", 5, "1920x1200", 1920);
        CachedImage middleFhd = Cached(MiddleId, "2026-09-17", 6, "1920x1080", 1920);
        CachedImage middleBig = Cached(MiddleId, "2026-09-17", 7, "1920x1080", 2560);   // same resolution label, wider file wins
        CachedImage oldestMissing = Cached(OldestId, "2026-09-14", 0);

        IReadOnlyList<CachedImage> candidates = RotationDecider.Candidates(
            [newestFhd, middleWide, oldestMissing, newestUhd, middleFhd, middleBig],
            "1920x1080",
            i => !ReferenceEquals(i, oldestMissing));

        Assert.Equal(2, candidates.Count);
        Assert.Same(newestFhd, candidates[0]);   // preferred resolution beats the wider UHD copy
        Assert.Same(middleBig, candidates[1]);   // among preferred-resolution copies the widest wins
        Assert.DoesNotContain(candidates, c => string.Equals(c.Id, OldestId, StringComparison.Ordinal));
    }

    [Fact]
    public void Candidates_NoPreferredCopy_FallsBackToWidestOfAnyResolution()
    {
        CachedImage wide = Cached(NewestId, "2026-09-20", 1, "1920x1200", 1920);
        CachedImage uhd = Cached(NewestId, "2026-09-20", 2, "UHD", 3840);

        IReadOnlyList<CachedImage> candidates = RotationDecider.Candidates([wide, uhd], "1920x1080", _ => true);

        Assert.Single(candidates);
        Assert.Same(uhd, candidates[0]);   // RESEARCH A8: any resolution of the same ID rather than nothing
    }
}
