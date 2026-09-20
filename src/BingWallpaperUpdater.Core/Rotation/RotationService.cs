using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;
using BingWallpaperUpdater.Core.Pipeline;
using BingWallpaperUpdater.Core.Ports;
using BingWallpaperUpdater.Core.Scheduling;

namespace BingWallpaperUpdater.Core.Rotation;

/// <summary>
/// The scheduled rotation core (RESEARCH Patterns 1, 2, 3, 4): one <c>RunTickAsync(reason)</c> pipeline —
/// fetch -> decide -> ensure -> apply -> persist -> re-arm — behind a <see cref="SemaphoreSlim"/>(1,1) single-flight
/// gate (D-01), driven by a 60 s <see cref="ITimer"/> heartbeat that compares <see cref="TimeProvider.GetUtcNow"/>
/// with the persisted absolute <see cref="AppState.NextDueUtc"/> (D-06). Re-arm is always <c>now + interval</c>, so
/// any number of missed intervals (sleep, hibernate, a stopped app) collapses into exactly one catch-up tick and never
/// a burst (ROT-04). Resume, clock and network signals only <see cref="Nudge"/> the heartbeat; they never run a tick
/// themselves. The only clock anywhere in <c>Rotation/</c> and <c>Scheduling/</c> is the injected
/// <see cref="TimeProvider"/>, so every scheduler rule runs under a fake clock in tests.
/// </summary>
public sealed class RotationService : IDisposable
{
    private readonly Settings _settings;
    private readonly AppState _state;
    private readonly string _statePath;
    private readonly CatalogService _catalog;
    private readonly ImageCache _cache;
    private readonly HttpGateway _http;
    private readonly IWallpaperApplier _applier;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly Random _random;

    // Single-flight gate (D-01). Deliberately never disposed: a heartbeat or Next may still hold it when Dispose runs,
    // and disposing a SemaphoreSlim under a waiter throws; a finished process reclaims it anyway.
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly object _sync = new();

    private ITimer? _heartbeat;

    // The retry ladder (D-13) lives in memory only — never in AppState / state.json — so a restart begins again at
    // 5 min and the persisted NextDueUtc always means the schedule, never a backoff. Both guarded by _sync.
    private DateTimeOffset? _retryDueUtc;   // in-memory backoff due time; null when no retry is pending
    private int _failureStage;               // 0 after any successful fetch; +1 per failed fetch
    private bool _startupTickDone;
    private volatile bool _disposed;
    private CancellationToken _ct;

    public RotationService(
        Settings settings,
        AppState state,
        string statePath,
        CatalogService catalog,
        ImageCache cache,
        HttpGateway http,
        IWallpaperApplier applier,
        IUiDispatcher ui,
        TimeProvider? time = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(statePath);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(ui);

        _settings = settings;
        _state = state;
        _statePath = statePath;
        _catalog = catalog;
        _cache = cache;
        _http = http;
        _applier = applier;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared;
    }

    /// <summary>True while a tick holds the gate; the tray reads it to disable "Next wallpaper" (D-05).</summary>
    public bool IsTickRunning => _gate.CurrentCount == 0;

    /// <summary>Mirrors <see cref="AppState.NextDueUtc"/>.</summary>
    public DateTimeOffset? NextDueUtc => _state.NextDueUtc;

    /// <summary>In-memory backoff due time; null when no retry is pending (D-13). Never persisted.</summary>
    public DateTimeOffset? RetryDueUtc
    {
        get
        {
            lock (_sync)
            {
                return _retryDueUtc;
            }
        }
    }

    /// <summary>Consecutive failed fetches: 0 after any successful fetch; 1 and 2 arm the 5 / 15 min retries, 3+ wait for the interval (D-13).</summary>
    public int FailureStage
    {
        get
        {
            lock (_sync)
            {
                return _failureStage;
            }
        }
    }

    /// <summary>
    /// Creates the heartbeat: the first callback after <paramref name="initialDelay"/> runs the
    /// <see cref="TickReason.Startup"/> tick (D-11; 0 s for a manual launch, 30-60 s for <c>--startup</c>, D-12) and
    /// every <see cref="ScheduleMath.HeartbeatPeriod"/> afterwards the schedule is checked.
    /// </summary>
    public void Start(TimeSpan initialDelay, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialDelay, TimeSpan.Zero);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_heartbeat is not null)
        {
            throw new InvalidOperationException("the rotation service is already started");
        }

        _ct = ct;
        _heartbeat = _time.CreateTimer(_ => OnHeartbeat(), null, initialDelay, ScheduleMath.HeartbeatPeriod);
    }

    /// <summary>
    /// Runs one tick. A caller that finds the gate held returns <see cref="TickResult.Busy"/> at once (no queueing,
    /// D-01); exceptions never escape (the heartbeat must survive), and cancellation is <see cref="TickResult.Cancelled"/>.
    /// </summary>
    public async Task<TickResult> RunTickAsync(TickReason reason, CancellationToken ct)
    {
        if (_disposed)
        {
            return TickResult.Cancelled;
        }

        if (!_gate.Wait(0, CancellationToken.None))
        {
            Log.Info($"tick skipped reason={reason} cause=busy");
            return TickResult.Busy;
        }

        try
        {
            return await TickCoreAsync(reason, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return TickResult.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Warn($"tick failed reason={reason} error={ex.Message}", ex);
            return TickResult.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Any thread: asks the heartbeat to check the schedule after <see cref="ScheduleMath.ResumeDebounce"/> (bursts coalesce, D-10).</summary>
    public void Nudge(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        if (_disposed)
        {
            return;
        }

        Log.Info($"schedule nudge source={source}");
        _heartbeat?.Change(ScheduleMath.ResumeDebounce, ScheduleMath.HeartbeatPeriod);
    }

    /// <summary>Any thread: the system clock changed — clamp a due time left more than one interval ahead (D-07), persist, nudge.</summary>
    public void OnClockChanged()
    {
        if (_disposed)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_sync)
        {
            _state.NextDueUtc = ScheduleMath.ClampAfterClockChange(now, _state.NextDueUtc, _settings.Interval);
        }

        TrySaveState();
        Nudge("time-changed");
    }

    /// <summary>
    /// Any thread: the network came back. A pending retry is pulled forward to <c>now + <see cref="ScheduleMath.ResumeDebounce"/></c>
    /// — deliberately the same constant <see cref="Nudge"/> re-arms the heartbeat to, so the nudged beat itself finds
    /// the retry due (any longer lead would make that beat see "not yet due" and slip the retry to the next 60 s beat).
    /// The clock is read before the nudge so the retry can never be later than the re-armed beat. Without a pending
    /// retry this is only a nudge; the <c>IsDue</c> gate keeps a flood of signals from producing a burst (T-02-08).
    /// </summary>
    public void OnNetworkAvailable()
    {
        if (_disposed)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_sync)
        {
            if (_retryDueUtc is not null)
            {
                _retryDueUtc = now + ScheduleMath.ResumeDebounce;
            }
        }

        Nudge("network");
    }

    /// <summary>Test/diagnostic helper: completes once no tick holds the gate.</summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _gate.Release();
    }

    /// <summary>Stops the heartbeat; later <see cref="RunTickAsync"/> calls return <see cref="TickResult.Cancelled"/>. Does not wait for an in-flight tick (RESEARCH Pitfall 9).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heartbeat?.Dispose();
        _heartbeat = null;
    }

    // ---- heartbeat ------------------------------------------------------------------------------------

    private void OnHeartbeat()
    {
        if (_disposed || IsTickRunning)
        {
            return; // a beat during an in-flight tick is a silent no-op; only Next logs "busy"
        }

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset? retryDue;
        lock (_sync)
        {
            _state.NextDueUtc = ScheduleMath.ClampAfterClockChange(now, _state.NextDueUtc, _settings.Interval);   // D-07, idempotent
            retryDue = _retryDueUtc;
        }

        if (!_startupTickDone)
        {
            _ = RunTickAsync(TickReason.Startup, _ct);
            return;
        }

        // Retry before Interval (D-13): a beat dispatches at most one tick, and both go through the same gate.
        if (retryDue is { } retry && now >= retry)
        {
            _ = RunTickAsync(TickReason.Retry, _ct);
            return;
        }

        if (ScheduleMath.IsDue(now, _state.NextDueUtc))
        {
            _ = RunTickAsync(TickReason.Interval, _ct);
        }
    }

    // ---- the tick -------------------------------------------------------------------------------------

    private async Task<TickResult> TickCoreAsync(TickReason reason, CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Log.Info($"tick reason={reason} mode={_settings.Mode} interval={_settings.IntervalMinutes}");

        bool intervalDue = ScheduleMath.IsDue(now, _state.NextDueUtc);   // captured BEFORE any re-arm

        // 1. fetch — one conditional GET per tick (D-02); null means neither source produced a row (Pitfall 5).
        CatalogEntry? entry = await _catalog.GetNewestAsync(_settings.Market, ct).ConfigureAwait(false);
        _state.LastCheckUtc = now;
        bool fetchFailed = entry is null;

        // 2. decide
        IReadOnlyList<CachedImage> candidates = RotationDecider.Candidates(
            _cache.Index.Images,
            _settings.Resolution,
            i => File.Exists(Path.Combine(_cache.CacheDir, i.File)));
        RotationDecision decision = RotationDecider.Decide(reason, _settings.IsRandomMode, entry, _state, candidates, intervalDue, _random);

        // 3. ensure
        string? path = null;
        ImageId id = default;
        if (decision.Kind == DecisionKind.ApplyNew)
        {
            CachedImage? cached = await _cache.EnsureAsync(decision.Entry!, _settings.Resolution, _http, ct).ConfigureAwait(false);
            if (cached is null)
            {
                fetchFailed = true;
                decision = RotationDecider.Decide(reason, _settings.IsRandomMode, null, _state, candidates, intervalDue, _random);
            }
            else
            {
                path = Path.GetFullPath(Path.Combine(_cache.CacheDir, cached.File));
                id = decision.Entry!.Id;
            }
        }

        if (decision.Kind is DecisionKind.StepOlder or DecisionKind.Random)
        {
            if (ImageId.TryParse(decision.Target!.Id, out id))
            {
                path = Path.GetFullPath(Path.Combine(_cache.CacheDir, decision.Target.File));
            }
            else
            {
                Log.Warn($"rotation skipped id={decision.Target.Id} reason=bad-id");
                decision = new RotationDecision(DecisionKind.NoOp, null, null, "bad-id");
            }
        }

        if (decision.Kind == DecisionKind.SeedLastSeen)
        {
            _state.LastSeenNewestId = decision.Entry!.Id.Value;
        }

        // 4. apply — awaitable STA hop; LastSeenNewestId is written only after ApplyStage reports Ok (D-03, T-02-06).
        TickResult result = TickResult.NoOp;
        if (path is not null)
        {
            string applyPath = path;
            ImageId applyId = id;
            ApplyResult apply = await _ui.InvokeAsync(
                () => ApplyStage.Run(_applier, applyPath, applyId, _cache, _state, _statePath, now), ct).ConfigureAwait(false);
            if (apply.Ok && decision.Kind == DecisionKind.ApplyNew)
            {
                _state.LastSeenNewestId = applyId.Value;
            }

            result = apply.Ok ? TickResult.Applied : TickResult.ApplyFailed;
        }

        // 5. re-arm
        DateTimeOffset? retryDue;
        lock (_sync)
        {
            // Next re-arms only when it applied (D-05); Startup/Interval re-arm when the schedule was due (RESEARCH A3,
            // Pitfall 4) — even after a failed apply, so a COM failure cannot make the heartbeat retry every minute;
            // Retry never touches the schedule (D-13).
            if ((reason == TickReason.Next && result == TickResult.Applied)
                || (reason != TickReason.Retry && ScheduleMath.IsDue(now, _state.NextDueUtc)))
            {
                _state.NextDueUtc = ScheduleMath.Rearm(now, _settings.Interval);
            }

            // The ladder (D-13): 5 min after the first failure, 15 min after the second, then nothing until the
            // interval-due tick — the stage keeps counting so later failures never shrink the wait back (T-02-08).
            // A random rotation that followed the failed fetch still reports Applied; the retry= token shows the failure.
            if (fetchFailed)
            {
                TimeSpan? delay = ScheduleMath.RetryDelay(_failureStage);
                _failureStage++;
                _retryDueUtc = delay is { } d ? now + d : null;
                if (_retryDueUtc is { } scheduled)
                {
                    Log.Info($"retry scheduled stage={_failureStage} at={scheduled:O}");
                }
                else
                {
                    Log.Info($"retry exhausted stage={_failureStage} next={_state.NextDueUtc?.ToString("O") ?? "-"}");
                }

                if (path is null)
                {
                    result = TickResult.FetchFailed;
                }
            }
            else
            {
                _failureStage = 0;
                _retryDueUtc = null;
            }

            retryDue = _retryDueUtc;
        }

        // 6. persist — after ApplyStage's own save, on every path including NoOp and FetchFailed (ROT-07). The retry
        // time is not part of AppState, so state.json never carries it.
        _startupTickDone = true;
        TrySaveState();
        Log.Info($"tick done reason={reason} result={result} decision={decision.Kind} why={decision.Why} next={_state.NextDueUtc?.ToString("O") ?? "-"} retry={retryDue?.ToString("O") ?? "-"}");
        return result;
    }

    private void TrySaveState()
    {
        try
        {
            _state.Save(_statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"state save failed path={_statePath}", ex);
        }
    }
}
