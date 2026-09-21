using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Pipeline;
using BingWallpaperUpdater.Core.Ports;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// WR-07: the eviction protection must be on disk before the desktop changes, a failed apply must leave the
/// record naming what is really shown, and <c>apply ok</c> must describe persisted state. The fake applier reads
/// <c>index.json</c> at the instant it is called. Joins the "LogSink" collection because log order is asserted.
/// </summary>
[Collection("LogSink")]
public sealed class ApplyStageTests : IDisposable
{
    private const string PreviousId = "OHR.Previous_EN-US1";
    private const string NewId = "OHR.AlphornBavaria_EN-US6200857270";

    private readonly string _dir;
    private readonly string _indexPath;
    private readonly string _statePath;
    private readonly string _logPath;
    private readonly string _imagePath;

    public ApplyStageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bwu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "index.json");
        _statePath = Path.Combine(_dir, "state.json");
        _logPath = Path.Combine(_dir, "log.txt");
        _imagePath = Path.Combine(_dir, "2026-09-20_AlphornBavaria_EN-US6200857270.jpg");
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

    private static ImageId Id(string value)
    {
        Assert.True(ImageId.TryParse(value, out ImageId id));
        return id;
    }

    private static CachedImage Cached(string id, string file) => new()
    {
        Id = id,
        Market = "EN-US",
        Resolution = "UHD",
        Width = 3840,
        Height = 2160,
        File = file,
        Bytes = 16,
        DownloadedUtc = new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero),
    };

    /// <summary>A cache whose index already has the previous image applied and the new one downloaded.</summary>
    private ImageCache SeededCache()
    {
        var index = new CacheIndex
        {
            Applied = [PreviousId],
            Images = [Cached(PreviousId, "2026-09-19_Previous_EN-US1.jpg"), Cached(NewId, Path.GetFileName(_imagePath))],
        };
        AtomicJsonFile.Save(_indexPath, index, CoreJsonContext.Default.CacheIndex);
        File.WriteAllBytes(Path.Combine(_dir, "2026-09-19_Previous_EN-US1.jpg"), new byte[16]);
        File.WriteAllBytes(_imagePath, new byte[16]);
        var cache = new ImageCache(_dir, _indexPath);
        cache.Load();
        return cache;
    }

    private List<string> AppliedOnDisk() =>
        AtomicJsonFile.Load(_indexPath, CoreJsonContext.Default.CacheIndex)?.Applied ?? [];

    /// <summary>Records what <c>index.json</c> said at the moment Apply ran, then answers as scripted.</summary>
    private sealed class ProbingApplier : IWallpaperApplier
    {
        private readonly Func<string, ApplyResult> _respond;
        private readonly Func<List<string>> _appliedOnDisk;

        public ProbingApplier(Func<List<string>> appliedOnDisk, Func<string, ApplyResult> respond)
        {
            _appliedOnDisk = appliedOnDisk;
            _respond = respond;
        }

        public List<string>? AppliedWhenCalled { get; private set; }
        public string? PathReceived { get; private set; }
        public int Calls { get; private set; }
        public int PerMonitorCalls { get; private set; }
        public IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)>? AssignmentsReceived { get; private set; }

        public ApplyResult Apply(string absolutePath)
        {
            Calls++;
            PathReceived = absolutePath;
            AppliedWhenCalled = _appliedOnDisk();
            return _respond(absolutePath);
        }

        public IReadOnlyList<MonitorHandle> GetAttachedMonitors() => [];

        public ApplyResult ApplyPerMonitor(IReadOnlyList<(MonitorHandle Monitor, string AbsolutePath)> assignments)
        {
            PerMonitorCalls++;
            AssignmentsReceived = assignments;
            PathReceived = assignments[0].AbsolutePath;
            AppliedWhenCalled = _appliedOnDisk();
            return _respond(assignments[0].AbsolutePath);
        }
    }

    private static readonly MonitorHandle Left = new(@"\\?\DISPLAY#TEST#1&0&UID1#{guid}", 1920, 1080, 0, 0);
    private static readonly MonitorHandle Right = new(@"\\?\DISPLAY#TEST#1&0&UID2#{guid}", 1920, 1080, 1920, 0);

    private string PreviousPath => Path.Combine(_dir, "2026-09-19_Previous_EN-US1.jpg");

    private static ApplyResult Ok(string path) => new(true, "com", path, "DWPOS_FILL", null);

    [Fact]
    public void Run_Success_ProtectionIsOnDiskBeforeApply_ThenStateSaved_ThenApplyOkLogged()
    {
        ImageCache cache = SeededCache();
        var state = new AppState();
        var applier = new ProbingApplier(AppliedOnDisk, Ok);

        ApplyResult result = ApplyStage.Run(applier, _imagePath, Id(NewId), cache, state, _statePath);

        Assert.True(result.Ok);
        Assert.Equal(1, applier.Calls);
        Assert.Equal(_imagePath, applier.PathReceived);
        Assert.Equal([NewId], applier.AppliedWhenCalled!); // persisted BEFORE the desktop changed
        Assert.Equal([NewId], AppliedOnDisk());
        Assert.Equal([NewId], cache.Index.Applied);

        AppState? savedState = AtomicJsonFile.Load(_statePath, CoreJsonContext.Default.AppState);
        Assert.NotNull(savedState);
        Assert.Equal(NewId, savedState!.CurrentImageId);
        Assert.NotNull(savedState.LastAppliedUtc);
        Assert.Equal(NewId, state.CurrentImageId);

        string log = LogText();
        Assert.Contains($"apply ok method=com id={NewId} path={_imagePath} readback={_imagePath} position=DWPOS_FILL", log);
        Assert.DoesNotContain("apply failed", log);
    }

    [Fact]
    public void Run_ApplierReportsFailure_AppliedIsRestoredToThePreviousImage_StateUntouched_NoApplyOk()
    {
        ImageCache cache = SeededCache();
        var state = new AppState { CurrentImageId = PreviousId };
        var applier = new ProbingApplier(AppliedOnDisk, _ => new ApplyResult(false, "com", null, null, "ArgumentException 0x80070057 bad path"));

        ApplyResult result = ApplyStage.Run(applier, _imagePath, Id(NewId), cache, state, _statePath);

        Assert.False(result.Ok);
        Assert.Equal([NewId], applier.AppliedWhenCalled!); // it was protected during the attempt...
        Assert.Equal([PreviousId], AppliedOnDisk());        // ...and rolled back once the attempt failed
        Assert.Equal([PreviousId], cache.Index.Applied);
        Assert.Equal(PreviousId, state.CurrentImageId);
        Assert.False(File.Exists(_statePath), "state.json must not be written for a failed apply");

        string log = LogText();
        Assert.Contains("apply failed method=com error=ArgumentException 0x80070057 bad path", log);
        Assert.DoesNotContain("apply ok", log);
    }

    [Fact]
    public void Run_ApplierThrows_AppliedIsRestored_AndTheExceptionPropagates()
    {
        ImageCache cache = SeededCache();
        var state = new AppState();
        var applier = new ProbingApplier(AppliedOnDisk, _ => throw new InvalidOperationException("adapter bug"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ApplyStage.Run(applier, _imagePath, Id(NewId), cache, state, _statePath));

        Assert.Equal("adapter bug", ex.Message);
        Assert.Equal([PreviousId], AppliedOnDisk());
        Assert.Equal([PreviousId], cache.Index.Applied);
        Assert.Null(state.CurrentImageId);
        Assert.DoesNotContain("apply ok", LogText());
    }

    [Fact]
    public void Run_MarkAppliedCannotBeSaved_DesktopIsNeverTouched_AndInMemoryAppliedIsRestored()
    {
        ImageCache cache = SeededCache();
        var state = new AppState();
        var applier = new ProbingApplier(AppliedOnDisk, Ok);

        // Lock index.json.tmp's destination so the atomic save fails: an exclusive handle on index.json itself.
        using FileStream lockHandle = new(_indexPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Exception? ex = Record.Exception(() => ApplyStage.Run(applier, _imagePath, Id(NewId), cache, state, _statePath));

        Assert.NotNull(ex);
        Assert.Equal(0, applier.Calls);
        Assert.Equal([PreviousId], cache.Index.Applied);
        Assert.Null(state.CurrentImageId);
        Assert.DoesNotContain("apply ok", LogText());
    }

    [Fact]
    public void Run_StateSaveFails_DesktopAndIndexStayConsistent_WarnedAndApplyOkStillLogged()
    {
        ImageCache cache = SeededCache();
        var state = new AppState();
        var applier = new ProbingApplier(AppliedOnDisk, Ok);
        // A directory in the way of state.json makes the save fail without touching the index.
        Directory.CreateDirectory(_statePath);

        ApplyResult result = ApplyStage.Run(applier, _imagePath, Id(NewId), cache, state, _statePath);

        Assert.True(result.Ok);
        Assert.Equal([NewId], AppliedOnDisk());
        Assert.Equal(NewId, state.CurrentImageId);

        string log = LogText();
        Assert.Contains("state save failed path=", log);
        Assert.Contains($"apply ok method=com id={NewId}", log);
        Assert.True(log.IndexOf("state save failed", StringComparison.Ordinal) < log.IndexOf("apply ok", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_FirstEverApply_NoPreviousApplied_FailureLeavesAppliedEmpty()
    {
        AtomicJsonFile.Save(_indexPath, new CacheIndex { Images = [Cached(NewId, Path.GetFileName(_imagePath))] }, CoreJsonContext.Default.CacheIndex);
        File.WriteAllBytes(_imagePath, new byte[16]);
        var cache = new ImageCache(_dir, _indexPath);
        cache.Load();
        var applier = new ProbingApplier(AppliedOnDisk, _ => new ApplyResult(false, "spi", null, null, "set returned FALSE"));

        ApplyResult result = ApplyStage.Run(applier, _imagePath, Id(NewId), cache, new AppState(), _statePath);

        Assert.False(result.Ok);
        Assert.Empty(AppliedOnDisk());
        Assert.Empty(cache.Index.Applied);
    }

    // ---- RunPerMonitor (WALL-03): the same four steps with N applied IDs -----------------------------------

    [Fact]
    public void RunPerMonitor_MarksBothIdsBeforeApply()
    {
        ImageCache cache = SeededCache();
        var state = new AppState();
        var applier = new ProbingApplier(AppliedOnDisk, Ok);
        List<(ImageId, string)> plan = [(Id(NewId), _imagePath), (Id(PreviousId), PreviousPath)];

        ApplyResult result = ApplyStage.RunPerMonitor(applier, [Left, Right], plan, cache, state, _statePath);

        Assert.True(result.Ok);
        Assert.Equal(0, applier.Calls);
        Assert.Equal(1, applier.PerMonitorCalls);
        Assert.Equal([NewId, PreviousId], applier.AppliedWhenCalled!);   // both persisted BEFORE the desktop changed
        Assert.Equal([NewId, PreviousId], AppliedOnDisk());
        Assert.Equal([NewId, PreviousId], cache.Index.Applied);
        Assert.Equal(2, applier.AssignmentsReceived!.Count);
        Assert.Same(Left, applier.AssignmentsReceived[0].Monitor);
        Assert.Equal(_imagePath, applier.AssignmentsReceived[0].AbsolutePath);
        Assert.Same(Right, applier.AssignmentsReceived[1].Monitor);
        Assert.Equal(PreviousPath, applier.AssignmentsReceived[1].AbsolutePath);
        Assert.Equal(NewId, state.CurrentImageId);
        Assert.NotNull(state.LastAppliedUtc);
        Assert.Equal(NewId, AtomicJsonFile.Load(_statePath, CoreJsonContext.Default.AppState)!.CurrentImageId);
    }

    [Fact]
    public void RunPerMonitor_FailedApply_RestoresPreviousApplied()
    {
        ImageCache cache = SeededCache();
        var state = new AppState { CurrentImageId = PreviousId };
        var applier = new ProbingApplier(AppliedOnDisk, _ => new ApplyResult(false, "com", null, null, "COMException 0x80004005 shell said no"));
        List<(ImageId, string)> plan = [(Id(NewId), _imagePath), (Id(PreviousId), PreviousPath)];

        ApplyResult result = ApplyStage.RunPerMonitor(applier, [Left, Right], plan, cache, state, _statePath);

        Assert.False(result.Ok);
        Assert.Equal([NewId, PreviousId], applier.AppliedWhenCalled!);
        Assert.Equal([PreviousId], AppliedOnDisk());
        Assert.Equal([PreviousId], cache.Index.Applied);
        Assert.Equal(PreviousId, state.CurrentImageId);
        Assert.False(File.Exists(_statePath));
        string log = LogText();
        Assert.Contains("apply failed method=com error=COMException 0x80004005 shell said no", log);
        Assert.DoesNotContain("apply ok", log);

        // An escaping exception rolls back the same way.
        var thrower = new ProbingApplier(AppliedOnDisk, _ => throw new InvalidOperationException("adapter bug"));
        Assert.Throws<InvalidOperationException>(() => ApplyStage.RunPerMonitor(thrower, [Left, Right], plan, cache, state, _statePath));
        Assert.Equal([PreviousId], AppliedOnDisk());
        Assert.Equal([PreviousId], cache.Index.Applied);
    }

    [Fact]
    public void RunPerMonitor_NoMonitors_FallsBackToSingleApply()
    {
        ImageCache cache = SeededCache();
        var state = new AppState();
        var applier = new ProbingApplier(AppliedOnDisk, Ok);
        List<(ImageId, string)> plan = [(Id(NewId), _imagePath), (Id(PreviousId), PreviousPath)];

        ApplyResult result = ApplyStage.RunPerMonitor(applier, [], plan, cache, state, _statePath);

        Assert.True(result.Ok);
        Assert.Equal(1, applier.Calls);
        Assert.Equal(0, applier.PerMonitorCalls);
        Assert.Equal(_imagePath, applier.PathReceived);
        Assert.Equal([NewId], applier.AppliedWhenCalled!);   // only the primary is on any desktop
        Assert.Equal([NewId], AppliedOnDisk());
        Assert.Equal(NewId, state.CurrentImageId);
        Assert.Contains($"apply ok method=com id={NewId} path={_imagePath} monitors=0 ids={NewId} readback={_imagePath} position=DWPOS_FILL", LogText());
    }

    [Fact]
    public void RunPerMonitor_LogsMonitorsAndIds()
    {
        ImageCache cache = SeededCache();
        var applier = new ProbingApplier(AppliedOnDisk, Ok);
        List<(ImageId, string)> plan = [(Id(NewId), _imagePath), (Id(PreviousId), PreviousPath)];

        ApplyStage.RunPerMonitor(applier, [Left, Right], plan, cache, new AppState(), _statePath);

        string log = LogText();
        Assert.Contains($"apply ok method=com id={NewId} path={_imagePath} monitors=2 ids={NewId},{PreviousId} readback={_imagePath} position=DWPOS_FILL", log);
        Assert.DoesNotContain("apply failed", log);
    }

    /// <summary>Three monitors, two images: the assignment index wraps by modulo, so the third monitor shows the primary again.</summary>
    [Fact]
    public void RunPerMonitor_MoreMonitorsThanPlanEntries_WrapsByModulo()
    {
        ImageCache cache = SeededCache();
        var applier = new ProbingApplier(AppliedOnDisk, Ok);
        var third = new MonitorHandle(@"\\?\DISPLAY#TEST#1&0&UID3#{guid}", 1920, 1080, 3840, 0);
        List<(ImageId, string)> plan = [(Id(NewId), _imagePath), (Id(PreviousId), PreviousPath)];

        ApplyStage.RunPerMonitor(applier, [Left, Right, third], plan, cache, new AppState(), _statePath);

        Assert.Equal([_imagePath, PreviousPath, _imagePath], applier.AssignmentsReceived!.Select(a => a.AbsolutePath).ToArray());
    }

    [Fact]
    public void RunPerMonitor_EmptyPlan_Throws()
    {
        ImageCache cache = SeededCache();
        var applier = new ProbingApplier(AppliedOnDisk, Ok);

        Assert.Throws<ArgumentException>(() => ApplyStage.RunPerMonitor(applier, [Left], [], cache, new AppState(), _statePath));
        Assert.Equal(0, applier.Calls + applier.PerMonitorCalls);
    }
}
