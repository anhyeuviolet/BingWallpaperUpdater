using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Display;
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
    private readonly IMonitorLayout _monitors;
    private readonly TimeProvider _time;
    private readonly Random _random;

    // Single-flight gate (D-01). Deliberately never disposed: a heartbeat or Next may still hold it when Dispose runs,
    // and disposing a SemaphoreSlim under a waiter throws; a finished process reclaims it anyway.
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly object _sync = new();

    private ITimer? _heartbeat;
    private ITimer? _displayTimer;           // one-shot DisplayChangeDebounce timer (WALL-03); created lazily under _sync

    // The retry ladder (D-13) lives in memory only — never in AppState / state.json — so a restart begins again at
    // 5 min and the persisted NextDueUtc always means the schedule, never a backoff. Both guarded by _sync.
    private DateTimeOffset? _retryDueUtc;   // in-memory backoff due time; null when no retry is pending
    private int _failureStage;               // 0 after any successful fetch; +1 per failed fetch
    private int _appliedIntervalMinutes;     // the interval the current NextDueUtc was computed with (ApplySettings, D-07)
    private LastErrorKind _lastError;        // what the last tick left for the window's last-error line (UI-04); guarded by _sync
    private string[] _lastAttachedSet = [];  // sorted device paths of the monitors the last apply set (WALL-03); UI thread only, never persisted
    private string _appliedMonitorMode;      // the MonitorMode the desktop currently reflects; ApplySettings re-applies when the setting differs
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
        IMonitorLayout monitors,
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
        ArgumentNullException.ThrowIfNull(monitors);
        if (!Settings.AllowedIntervals.Contains(settings.IntervalMinutes))
        {
            // Settings.LoadOrCreate sanitises the file, so this is a programming error; rejecting it here keeps a
            // non-positive interval away from ScheduleMath, which throws on it (WR-02).
            throw new ArgumentOutOfRangeException(nameof(settings), settings.IntervalMinutes, "IntervalMinutes must be one of Settings.AllowedIntervals");
        }

        _settings = settings;
        _state = state;
        _statePath = statePath;
        _catalog = catalog;
        _cache = cache;
        _http = http;
        _applier = applier;
        _ui = ui;
        _monitors = monitors;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        _appliedIntervalMinutes = settings.IntervalMinutes;
        _appliedMonitorMode = settings.MonitorMode;
    }

    /// <summary>
    /// Any thread — raised after every tick and every re-apply once the gate is released (so a subscriber's
    /// <see cref="Snapshot"/> reads <c>TickRunning == false</c>) and at the end of <see cref="ApplySettings"/> before
    /// any forced re-apply it starts; never at gate acquisition. The Settings window subscribes while open and
    /// marshals to its own thread (UI-04). A throwing handler is logged and can never break the tick.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>True while a tick or a re-apply holds the gate; the tray reads it to disable "Next wallpaper" (D-05).</summary>
    public bool IsTickRunning => _gate.CurrentCount == 0;

    /// <summary>
    /// The read-only view for the Settings window (UI-04): schedule fields and the last-error kind under <c>_sync</c>,
    /// the current image's metadata looked up by <see cref="AppState.CurrentImageId"/> in the cache index (a benign
    /// reference read of a list <c>Add</c> replaces wholesale — never written from here).
    /// </summary>
    public RotationSnapshot Snapshot()
    {
        lock (_sync)
        {
            string? id = _state.CurrentImageId;
            CachedImage? current = id is null
                ? null
                : _cache.Index.Images.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
            return new RotationSnapshot(current?.Title, current?.Copyright, current?.Date, _state.LastCheckUtc, _state.NextDueUtc, IsTickRunning, _lastError);
        }
    }

    /// <summary>Mirrors <see cref="AppState.NextDueUtc"/> (read under the lock its writers hold, WR-03).</summary>
    public DateTimeOffset? NextDueUtc => ReadNextDue();

    /// <summary>
    /// The interval every schedule computation uses: the last value <see cref="ApplySettings"/> accepted (or the
    /// constructor validated), never the raw <see cref="Settings.IntervalMinutes"/>, so a value the settings window
    /// wrote outside <see cref="Settings.AllowedIntervals"/> can never reach <see cref="ScheduleMath"/> (WR-02).
    /// Read under <c>_sync</c> wherever it feeds a due time.
    /// </summary>
    private TimeSpan Interval => TimeSpan.FromMinutes(_appliedIntervalMinutes);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
            RaiseStateChanged();   // after the release: a subscriber's Snapshot() must read TickRunning == false
        }
    }

    /// <summary>Invokes <see cref="StateChanged"/> on the calling thread; a subscriber that throws is logged, never propagated (T-03-04).</summary>
    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("state changed handler failed", ex);
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
        try
        {
            _heartbeat?.Change(ScheduleMath.ResumeDebounce, ScheduleMath.HeartbeatPeriod);
        }
        catch (ObjectDisposedException)
        {
            // Dispose raced a resume/network/clock signal already running on its own thread (the unsubscribe does
            // not wait for in-flight handlers); there is nothing left to nudge (IN-01).
        }
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
            _state.NextDueUtc = ScheduleMath.ClampAfterClockChange(now, _state.NextDueUtc, Interval);
        }

        TrySaveState();
        Nudge("time-changed");
    }

    /// <summary>
    /// Any thread (Phase 3's settings window): the shared <see cref="Settings"/> instance this service was constructed
    /// with has been mutated and saved by the caller — the service never re-reads <c>settings.json</c> itself (D-09).
    /// When <see cref="Settings.IntervalMinutes"/> changed, the schedule is recomputed from the last apply rather than
    /// restarted: <c>NextDueUtc = LastAppliedUtc + newInterval</c>, floored at <c>now + 5 s</c> (D-07,
    /// <see cref="ScheduleMath.AfterIntervalChange"/>). A mode change needs no bookkeeping because every tick reads
    /// <see cref="Settings.IsRandomMode"/> live. Persists, logs <c>settings applied ...</c>, then nudges so a shortened
    /// interval that is already due runs within the debounce instead of up to 60 s later. Safe to call repeatedly.
    /// An interval outside <see cref="Settings.AllowedIntervals"/> is logged and ignored: the schedule keeps running
    /// on the last accepted interval (WR-02).
    /// </summary>
    public void ApplySettings()
    {
        if (_disposed)
        {
            return;
        }

        int requested = _settings.IntervalMinutes;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_sync)
        {
            if (requested != _appliedIntervalMinutes)
            {
                if (Settings.AllowedIntervals.Contains(requested))
                {
                    _state.NextDueUtc = ScheduleMath.AfterIntervalChange(_state.LastAppliedUtc, now, TimeSpan.FromMinutes(requested));
                    _appliedIntervalMinutes = requested;
                }
                else
                {
                    Log.Warn($"settings rejected field=IntervalMinutes value={requested} keeping={_appliedIntervalMinutes}");
                }
            }
        }

        TrySaveState();

        bool modeChanged;
        lock (_sync)
        {
            modeChanged = !string.Equals(_settings.MonitorMode, _appliedMonitorMode, StringComparison.Ordinal);
            if (modeChanged)
            {
                _appliedMonitorMode = _settings.MonitorMode;
            }
        }

        Log.Info($"settings applied interval={_appliedIntervalMinutes} mode={_settings.Mode} resolution={_settings.Resolution} market={_settings.Market} monitors={_settings.MonitorMode} language={_settings.Language} next={ReadNextDue()?.ToString("O") ?? "-"}");
        Nudge("settings");
        RaiseStateChanged();

        // A monitor-mode switch is visible within seconds, not at the next tick (criterion 2 "applies immediately"):
        // per-monitor -> the plan over the current image; same -> the current image on every monitor. Forced, so the
        // attached-set comparison does not suppress it; the gate still wins when a tick is in flight. Started LAST:
        // the re-apply's own finally raises after its release, so the raise above reflects the schedule only and
        // the window never observes a transient gate hold that nothing clears (CR-01).
        if (modeChanged)
        {
            _ = RunReapplyAsync("settings", force: true);
        }
    }

    /// <summary>
    /// Any thread (the tray's <c>SystemEvents.DisplaySettingsChanged</c> bridge): arms — or re-arms — the one-shot
    /// <see cref="ScheduleMath.DisplayChangeDebounce"/> timer on the injected clock, so a dock/undock burst (and the
    /// DPI / resolution changes that raise the same event) collapses into one re-apply check (WALL-03, T-03-17).
    /// Never runs a tick and never touches the schedule.
    /// </summary>
    public void OnDisplayChanged()
    {
        if (_disposed)
        {
            return;
        }

        Log.Info("display changed");
        try
        {
            lock (_sync)
            {
                _displayTimer ??= _time.CreateTimer(_ => OnDisplayDebounce(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            _displayTimer.Change(ScheduleMath.DisplayChangeDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Dispose raced a display signal (IN-01); nothing left to re-apply.
        }
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
        _displayTimer?.Dispose();
        _displayTimer = null;
    }

    // ---- heartbeat ------------------------------------------------------------------------------------

    private void OnHeartbeat()
    {
        // A System.Threading.Timer callback runs on a thread-pool thread: anything that escapes it is an unhandled
        // exception that terminates the process. RunTickAsync never throws; this guard covers the beat itself (WR-02).
        try
        {
            OnHeartbeatCore();
        }
        catch (Exception ex)
        {
            Log.Warn("heartbeat failed", ex);
        }
    }

    private void OnHeartbeatCore()
    {
        if (_disposed || IsTickRunning)
        {
            return; // a beat during an in-flight tick is a silent no-op; only Next logs "busy"
        }

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset? retryDue;
        DateTimeOffset? nextDue;
        lock (_sync)
        {
            _state.NextDueUtc = ScheduleMath.ClampAfterClockChange(now, _state.NextDueUtc, Interval);   // D-07, idempotent
            nextDue = _state.NextDueUtc;
            retryDue = _retryDueUtc;
        }

        if (!_startupTickDone)
        {
            // Set at dispatch, not on completion: a Startup tick that fails, throws or loses the gate to a Next click is
            // never replayed on the next beat (D-11); a still-due NextDueUtc is picked up by the Interval branch below.
            _startupTickDone = true;
            _ = RunTickAsync(TickReason.Startup, _ct);
            return;
        }

        // Retry before Interval (D-13): a beat dispatches at most one tick, and both go through the same gate.
        if (retryDue is { } retry && now >= retry)
        {
            _ = RunTickAsync(TickReason.Retry, _ct);
            return;
        }

        if (ScheduleMath.IsDue(now, nextDue))
        {
            _ = RunTickAsync(TickReason.Interval, _ct);
        }
    }

    // ---- the tick -------------------------------------------------------------------------------------

    private async Task<TickResult> TickCoreAsync(TickReason reason, CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();

        // The effective resolution is fixed once per tick, BEFORE the decider and the download stage see it (SRC-07,
        // Pitfall 3): "Auto" would throw in BingImageUrl.MinDimensions / CacheFileName.For, so nothing below reads
        // the raw setting — only this local.
        IReadOnlyList<(int Width, int Height)> sizes = MonitorSizes();
        string resolution = EffectiveResolution(sizes);
        string autoToken = "-";
        if (_settings.IsAutoResolution && sizes.Count > 0)
        {
            (int largestWidth, int largestHeight) = sizes.MaxBy(m => (long)m.Width * m.Height);
            autoToken = $"{largestWidth}x{largestHeight}";
        }

        Log.Info($"tick reason={reason} mode={_settings.Mode} interval={_appliedIntervalMinutes} resolution={resolution} auto={autoToken}");

        bool intervalDue = ScheduleMath.IsDue(now, ReadNextDue());   // captured BEFORE any re-arm

        // Everything step 5/6 reads is declared here so the tail runs whatever steps 1-4 did — including throwing
        // (G-01 / CR-01): before this try/catch an exception skipped the re-arm, the ladder and the save, and the
        // heartbeat re-dispatched a full tick (two network requests plus the failing step) every 60 s.
        bool fetchFailed = false;
        bool threw = false;
        string? path = null;
        ImageId id = default;
        RotationDecision? decision = null;
        ApplyResult? apply = null;   // kept for the last-error classification (UI-04): read-back mismatch is not a failure
        TickResult result = TickResult.NoOp;

        try
        {
            // 1. fetch — one conditional GET per tick (D-02); null means neither source produced a row (Pitfall 5).
            CatalogEntry? entry = await _catalog.GetNewestAsync(_settings.Market, ct).ConfigureAwait(false);
            lock (_sync)
            {
                _state.LastCheckUtc = now;   // a 24-byte DateTimeOffset?: never torn by a concurrent save (WR-03)
            }

            fetchFailed = entry is null;

            // 2. decide
            IReadOnlyList<CachedImage> candidates = RotationDecider.Candidates(_cache.Index.Images, resolution, FileExists);
            decision = RotationDecider.Decide(reason, _settings.IsRandomMode, entry, _state, candidates, intervalDue, _random);

            // 3. ensure
            if (decision.Kind == DecisionKind.ApplyNew)
            {
                CachedImage? cached = await _cache.EnsureAsync(decision.Entry!, resolution, _http, ct).ConfigureAwait(false);
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
            if (path is not null)
            {
                string applyPath = path;
                ImageId applyId = id;
                if (_settings.IsPerMonitor)
                {
                    // WALL-03: the candidates are recomputed AFTER the ensure step (it may have just added the new
                    // image); monitors are enumerated inside the STA hop, at apply time, never earlier or persisted.
                    IReadOnlyList<CachedImage> planCandidates = RotationDecider.Candidates(_cache.Index.Images, resolution, FileExists);
                    apply = await _ui.InvokeAsync(
                        () => ApplyPlan(_applier.GetAttachedMonitors(), planCandidates, applyId, applyPath, now), ct).ConfigureAwait(false);
                }
                else
                {
                    apply = await _ui.InvokeAsync(
                        () => ApplyStage.Run(_applier, applyPath, applyId, _cache, _state, _statePath, now), ct).ConfigureAwait(false);
                }

                if (apply.Ok && decision.Kind == DecisionKind.ApplyNew)
                {
                    _state.LastSeenNewestId = applyId.Value;
                }

                result = apply.Ok ? TickResult.Applied : TickResult.ApplyFailed;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown leaves the schedule alone (RESEARCH Pitfall 9); RunTickAsync maps it to Cancelled
        }
        catch (Exception ex)
        {
            // A throw (index.json / state.json write failure, an escaping applier exception, an
            // UnauthorizedAccessException from the download stream) is scheduled by the same D-13 ladder as a failed
            // fetch — no second ladder, no special case. That includes a TaskCanceledException that is NOT the tick's
            // own token (HttpClient.Timeout, Task.WaitAsync(TimeSpan), a linked-CTS timeout): only ct decides what
            // counts as cancellation, otherwise such a throw would skip the re-arm and reopen the CR-01 loop (WR-04).
            threw = true;
            fetchFailed = true;
            Log.Warn($"tick failed reason={reason} error={ex.Message}", ex);
        }

        // 5. re-arm — runs on every non-cancelled path, including a tick that threw.
        DateTimeOffset? retryDue;
        DateTimeOffset? nextDue;
        lock (_sync)
        {
            // Next re-arms only when it applied (D-05); Startup/Interval re-arm when the schedule was due (RESEARCH A3,
            // Pitfall 4) — even after a failed OR throwing apply, so neither a COM failure nor an exception can make
            // the heartbeat retry every minute; Retry never touches the schedule (D-13).
            if ((reason == TickReason.Next && result == TickResult.Applied)
                || (reason != TickReason.Retry && ScheduleMath.IsDue(now, _state.NextDueUtc)))
            {
                _state.NextDueUtc = ScheduleMath.Rearm(now, Interval);
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

            // The window's last-error line (UI-04, locked ROADMAP note): Apply beats Fetch beats ReadBack; a clean tick
            // clears it. A read-back mismatch is Ok for the pipeline (ApplyStage policy) but shown to the user.
            _lastError = threw || result == TickResult.ApplyFailed ? LastErrorKind.Apply
                : fetchFailed ? LastErrorKind.Fetch
                : apply is { Ok: true, ReadBackPath: { } readBack } && path is not null && !string.Equals(readBack, path, StringComparison.OrdinalIgnoreCase) ? LastErrorKind.ReadBack
                : LastErrorKind.None;

            retryDue = _retryDueUtc;
            nextDue = _state.NextDueUtc;
        }

        if (threw)
        {
            result = TickResult.Failed;   // Failed wins over whatever the ladder branch set (FetchFailed / NoOp)
        }

        // 6. persist — after ApplyStage's own save, on every path including NoOp, FetchFailed and Failed (ROT-07). The
        // retry time is not part of AppState, so state.json never carries it. decision is null only when the throw
        // preceded step 2, hence the "-" tokens.
        TrySaveState();
        Log.Info($"tick done reason={reason} result={result} decision={decision?.Kind.ToString() ?? "-"} why={decision?.Why ?? "-"} next={nextDue?.ToString("O") ?? "-"} retry={retryDue?.ToString("O") ?? "-"}");
        return result;
    }

    private DateTimeOffset? ReadNextDue()
    {
        lock (_sync)
        {
            return _state.NextDueUtc;
        }
    }

    // ---- per-monitor apply (WALL-03) --------------------------------------------------------------------

    /// <summary>The decider's file predicate: a cache entry is a candidate only while its file is on disk.</summary>
    private bool FileExists(CachedImage image) => File.Exists(Path.Combine(_cache.CacheDir, image.File));

    /// <summary>
    /// UI (STA) thread only: over the monitors enumerated NOW by the caller, builds the deterministic
    /// <see cref="MonitorPlan"/> (primary first, next older neighbours, same-on-all until enough are cached) and runs
    /// <see cref="ApplyStage.RunPerMonitor"/>. With no enumerable monitor the plan has one entry and the stage
    /// degrades to the NULL-monitor apply. Records the attached device-path set in memory (never persisted) so a
    /// display change can tell "something attached or detached" from a mere DPI / resolution change.
    /// </summary>
    private ApplyResult ApplyPlan(IReadOnlyList<MonitorHandle> monitors, IReadOnlyList<CachedImage> candidates, ImageId primaryId, string primaryPath, DateTimeOffset appliedUtc)
    {
        IReadOnlyList<string> ids = MonitorPlan.Build(candidates, primaryId.Value, Math.Max(1, monitors.Count));

        var plan = new List<(ImageId Id, string AbsolutePath)>(ids.Count);
        foreach (string rawId in ids)
        {
            if (string.Equals(rawId, primaryId.Value, StringComparison.Ordinal))
            {
                plan.Add((primaryId, primaryPath));
                continue;
            }

            CachedImage? candidate = candidates.FirstOrDefault(c => string.Equals(c.Id, rawId, StringComparison.Ordinal));
            if (candidate is null || !ImageId.TryParse(rawId, out ImageId parsed))
            {
                // Cannot happen for a plan built from these candidates; a stale index entry with a malformed ID
                // gets the primary instead of failing the whole apply.
                Log.Warn($"monitor plan skipped id={rawId} reason=bad-id");
                plan.Add((primaryId, primaryPath));
                continue;
            }

            plan.Add((parsed, Path.GetFullPath(Path.Combine(_cache.CacheDir, candidate.File))));
        }

        ApplyResult result = ApplyStage.RunPerMonitor(_applier, monitors, plan, _cache, _state, _statePath, appliedUtc);
        _lastAttachedSet = DeviceSet(monitors);
        return result;
    }

    /// <summary>The attached monitors as a sorted device-path array (order-independent comparison of two enumerations).</summary>
    private static string[] DeviceSet(IReadOnlyList<MonitorHandle> monitors) =>
        monitors.Select(m => m.DevicePath).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    // ---- re-apply (WALL-03 dock/undock, mode switch) ----------------------------------------------------

    private void OnDisplayDebounce()
    {
        // Timer callback on a thread-pool thread: nothing may escape (WR-02). RunReapplyAsync never throws.
        try
        {
            _ = RunReapplyAsync("display", force: false);
        }
        catch (Exception ex)
        {
            Log.Warn("display debounce failed", ex);
        }
    }

    /// <summary>
    /// Re-applies what is already current — no fetch, no decision, no schedule change, no retry ladder — through the
    /// same single-flight gate as ticks. <paramref name="force"/> is false for a display change (per-monitor mode only,
    /// and only when the attached device-path set differs from the one the last apply recorded, so a DPI / resolution
    /// change re-sets nothing) and true for a monitor-mode switch (either direction, whatever the set). The resolution
    /// comes from the same <see cref="EffectiveResolution"/> helper as the tick, so "Auto" can never leak in here.
    /// <see cref="AppState.LastAppliedUtc"/> is kept: a re-apply is not a rotation and must not move the baseline an
    /// interval change is computed from. Every outcome is one log line: <c>reapply reason=&lt;r&gt; monitors=&lt;n&gt;
    /// result=&lt;Applied|Failed&gt;</c>, <c>reapply skipped reason=&lt;r&gt; cause=&lt;unchanged|same-mode|busy|no-current&gt;</c>
    /// or <c>reapply failed reason=&lt;r&gt; error=</c>; it is never re-armed.
    /// </summary>
    private async Task RunReapplyAsync(string reason, bool force)
    {
        if (_disposed)
        {
            return;
        }

        if (!force && !_settings.IsPerMonitor)
        {
            Log.Info($"reapply skipped reason={reason} cause=same-mode");
            return;
        }

        if (!_gate.Wait(0, CancellationToken.None))
        {
            Log.Info($"reapply skipped reason={reason} cause=busy");   // the running tick applies the current plan anyway
            return;
        }

        try
        {
            string? currentId;
            DateTimeOffset appliedUtc;
            lock (_sync)
            {
                currentId = _state.CurrentImageId;
                appliedUtc = _state.LastAppliedUtc ?? _time.GetUtcNow();
            }

            string resolution = EffectiveResolution(MonitorSizes());
            IReadOnlyList<CachedImage> candidates = RotationDecider.Candidates(_cache.Index.Images, resolution, FileExists);
            CachedImage? current = currentId is null
                ? null
                : candidates.FirstOrDefault(c => string.Equals(c.Id, currentId, StringComparison.Ordinal));
            if (current is null || !ImageId.TryParse(current.Id, out ImageId primaryId))
            {
                Log.Info($"reapply skipped reason={reason} cause=no-current");
                return;
            }

            string primaryPath = Path.GetFullPath(Path.Combine(_cache.CacheDir, current.File));
            (ApplyResult? apply, int monitorCount) = await _ui.InvokeAsync(() =>
            {
                IReadOnlyList<MonitorHandle> monitors = _applier.GetAttachedMonitors();
                string[] set = DeviceSet(monitors);
                if (!force && set.SequenceEqual(_lastAttachedSet, StringComparer.Ordinal))
                {
                    Log.Info($"reapply skipped reason={reason} cause=unchanged");
                    return ((ApplyResult?)null, monitors.Count);
                }

                ApplyResult result;
                if (_settings.IsPerMonitor)
                {
                    result = ApplyPlan(monitors, candidates, primaryId, primaryPath, appliedUtc);
                }
                else
                {
                    result = ApplyStage.Run(_applier, primaryPath, primaryId, _cache, _state, _statePath, appliedUtc);
                    _lastAttachedSet = set;
                }

                return (result, monitors.Count);
            }, _ct).ConfigureAwait(false);

            if (apply is not null)
            {
                Log.Info($"reapply reason={reason} monitors={monitorCount} result={(apply.Ok ? "Applied" : "Failed")}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"reapply failed reason={reason} error={ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
            RaiseStateChanged();   // after the release: a subscriber's Snapshot() must read TickRunning == false (CR-01)
        }
    }

    /// <summary>
    /// The attached monitors from the <see cref="IMonitorLayout"/> port, or an empty list when the adapter throws
    /// (a WinForms / display hiccup then degrades "Auto" to UHD with a warning instead of a Failed tick).
    /// </summary>
    private IReadOnlyList<(int Width, int Height)> MonitorSizes()
    {
        try
        {
            return _monitors.Sizes();
        }
        catch (Exception ex)
        {
            Log.Warn("monitor layout failed", ex);
            return [];
        }
    }

    /// <summary>
    /// The one place the raw resolution setting is read for the pipeline (SRC-07): "Auto" becomes a concrete value
    /// through <see cref="ResolutionPolicy"/>, an explicit value passes through unchanged. Pure — cannot throw on a
    /// sanitised setting. Every consumer (the decider, <c>EnsureAsync</c>, the tick log line, and Plan 03-04's
    /// re-apply) must go through this helper rather than the setting itself, so "Auto" can never leak into
    /// <c>BingImageUrl.MinDimensions</c>, <c>CacheFileName</c> or a cache entry.
    /// </summary>
    private string EffectiveResolution(IReadOnlyList<(int Width, int Height)> sizes) => ResolutionPolicy.Resolve(_settings.Resolution, sizes);

    /// <summary>
    /// Serialises <see cref="AppState"/> under <c>_sync</c> (WR-03): the schedule fields are written under that lock
    /// from the timer thread, the tick, <see cref="OnClockChanged"/> and <see cref="ApplySettings"/>, so an unlocked
    /// save could read a torn <c>DateTimeOffset?</c> and two concurrent saves would collide on <c>state.json.tmp</c>
    /// (<c>FileShare.None</c>), silently dropping the loser's schedule.
    /// </summary>
    private void TrySaveState()
    {
        try
        {
            lock (_sync)
            {
                _state.Save(_statePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"state save failed path={_statePath}", ex);
        }
    }
}
