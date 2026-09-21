using BingWallpaperUpdater.Core.Display;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// WALL-03 assignment rule, pure: primary first, next older neighbours wrapping, same-on-all until enough distinct
/// candidates are cached, deterministic. No files, no log.
/// </summary>
public sealed class MonitorPlanTests
{
    private const string Newest = "OHR.AlphornBavaria_EN-US6200857270";
    private const string Middle = "OHR.IcyCubs_EN-US5222104616";
    private const string Oldest = "OHR.MisurinaPeak_EN-US4897144498";
    private const string Stranger = "OHR.ParisSunset_EN-US6532307523";

    private static CachedImage Image(string id) => new() { Id = id, Market = "EN-US", File = $"{id}.jpg" };

    private static IReadOnlyList<CachedImage> NewestFirst(params string[] ids) => ids.Select(Image).ToList();

    [Fact]
    public void Build_OneImageTwoMonitors_SameImageOnBoth()
    {
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest), Newest, 2);

        Assert.Equal([Newest, Newest], plan);
    }

    [Fact]
    public void Build_TwoImagesTwoMonitors_PrimaryThenOther()
    {
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest, Middle), Newest, 2);

        Assert.Equal([Newest, Middle], plan);
    }

    [Fact]
    public void Build_ThreeImagesTwoMonitors_PrimaryInTheMiddle_NextOlderFollows()
    {
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest, Middle, Oldest), Middle, 2);

        Assert.Equal([Middle, Oldest], plan);
    }

    [Fact]
    public void Build_PrimaryIsOldest_WrapsToNewest()
    {
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest, Middle, Oldest), Oldest, 2);

        Assert.Equal([Oldest, Newest], plan);
    }

    [Fact]
    public void Build_ThreeImagesThreeMonitors_AllDistinctPrimaryFirst()
    {
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest, Middle, Oldest), Middle, 3);

        Assert.Equal([Middle, Oldest, Newest], plan);
        Assert.Equal(3, plan.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The adjacency edge: one distinct candidate short of the monitor count means every monitor gets the primary.</summary>
    [Fact]
    public void Build_OneFewerCandidateThanMonitors_EveryMonitorGetsPrimary_NoPartialMix()
    {
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest, Middle), Newest, 3);

        Assert.Equal([Newest, Newest, Newest], plan);
    }

    [Fact]
    public void Build_PrimaryAbsentFromCandidates_PrimaryFirstThenTheNewestOnes()
    {
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest, Middle, Oldest), Stranger, 3);

        Assert.Equal([Stranger, Newest, Middle], plan);
    }

    [Fact]
    public void Build_OneMonitor_JustThePrimary()
    {
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest, Middle, Oldest), Middle, 1);

        Assert.Equal([Middle], plan);
    }

    [Fact]
    public void Build_DuplicateIds_AreCollapsedBeforeCounting()
    {
        // Two resolutions of the same image are one candidate: not enough for two monitors.
        IReadOnlyList<string> plan = MonitorPlan.Build(NewestFirst(Newest, Newest), Newest, 2);

        Assert.Equal([Newest, Newest], plan);
    }

    [Fact]
    public void Build_ZeroOrNegativeMonitors_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitorPlan.Build(NewestFirst(Newest), Newest, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitorPlan.Build(NewestFirst(Newest), Newest, -1));
        Assert.Throws<ArgumentNullException>(() => MonitorPlan.Build(null!, Newest, 1));
        Assert.Throws<ArgumentException>(() => MonitorPlan.Build(NewestFirst(Newest), string.Empty, 1));
    }

    [Fact]
    public void Build_IsDeterministic_ForEqualInputs()
    {
        IReadOnlyList<CachedImage> candidates = NewestFirst(Newest, Middle, Oldest);

        IReadOnlyList<string> first = MonitorPlan.Build(candidates, Middle, 3);
        IReadOnlyList<string> second = MonitorPlan.Build(NewestFirst(Newest, Middle, Oldest), Middle, 3);

        Assert.Equal(first, second);
    }
}
