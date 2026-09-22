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
    // Catalog IDs whose backfill download was rejected in this process (CDN 404 placeholder, bad dimensions): never
    // persisted, so a restart retries each once (CACHE-06). Tick-only access under the single-flight gate, so no lock.
    private readonly HashSet<string> _backfillSkip = new(StringComparer.Ordinal);
    private int _appliedIntervalMinutes;     // the interval the current NextDueUtc was computed with (ApplySettings, D-07)
    private LastErrorKind _lastError;        // what the last tick left for the window's last-error line (UI-04); guarded by _sync
    private string[] _lastAttachedSet = [];  // sorted device paths of the monitors the last SUCCESSFUL apply set (WALL-03, WR-04); UI thread only, never persisted
    // The MonitorMode the desktop currently reflects — written only by the constructor and after a SUCCESSFUL apply
    // (TickCoreAsync step 4, RunReapplyAsync); a skipped or failed re-apply never commits it, so the next ApplySettings
    // still sees the difference and retries (WR-01). Guarded by _sync.
    private string _appliedMonitorMode;
    // Set by a forced mode re-apply BEFORE its gate attempt, cleared when a forced re-apply owns the gate and by
    // ReapplyIfPending: one of the two deferred pieces of work in the service (never a tick, D-01). Guarded by _sync.
    private bool _modeReapplyPending;
    // The display twin (WR-05): set by a display-change re-apply BEFORE its gate attempt, cleared when one owns the
    // gate and by ReapplyIfPending, so a dock/undock whose debounce fires while a tick holds the gate is re-applied at
    // the holder's release instead of waiting for the next display signal or the next applying tick. Guarded by _sync.
    private bool _displayReapplyPending;
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
            RaiseStateChanged();     // after the release: a subscriber's Snapshot() must read TickRunning == false
            ReapplyIfPending();      // after the release (its own Wait(0) would report busy) and after the raise (the window already knows the tick ended)
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

    /// <summary>
    /// Runs after every gate release (a tick's or a re-apply's). The only deferred work in the service — a re-apply
    /// that met a held gate (<c>reapply skipped ... cause=busy</c>) — is run now, so the desktop changes within
    /// seconds of the holder finishing instead of at the next new image: a forced monitor-mode switch the user asked
    /// for (WR-01), or a dock/undock whose debounce fired during the hold (WR-05). Both flags are consumed either
    /// way — equal modes mean the holder already applied in the new mode, and a display re-apply compares the
    /// attached set itself, so it is <c>cause=unchanged</c> when the holder's apply already covered the new monitors.
    /// A pending mode switch wins: the forced re-apply applies over the monitors attached now and commits that set,
    /// so the display flag needs no separate dispatch. Ticks are never deferred (D-01), and a re-apply that owns the
    /// gate clears its own flag, so a failed or no-current re-apply never re-dispatches itself (T-03-22). The
    /// bound, not an absolute: with a real (asynchronous) dispatcher a forced call whose <c>Wait(0)</c> beats the
    /// follow-up this method dispatched can see that follow-up's flag in its own finally and run at most one more
    /// bounded attempt (IN-11) — that attempt clears its flag and nothing re-sets it, so there is never a loop.
    /// </summary>
    private void ReapplyIfPending()
    {
        // Shutdown cancels the token before Dispose runs (TrayApplicationContext.Shutdown), so a tick cancelled at
        // shutdown reaches this finally with _disposed still false: without the token check it would dispatch a
        // re-apply whose UI hop the dispatcher cancels at once, logged as a failure (IN-10).
        if (_disposed || _ct.IsCancellationRequested)
        {
            return;
        }

        bool mode;
        bool display;
        lock (_sync)
        {
            mode = _modeReapplyPending && !string.Equals(_settings.MonitorMode, _appliedMonitorMode, StringComparison.Ordinal);
            display = _displayReapplyPending && _settings.IsPerMonitor;
            _modeReapplyPending = false;
            _displayReapplyPending = false;
        }

        if (!mode && !display)
        {
            return;
        }

        // Fire-and-forget from a finally on a thread-pool continuation. Async method: any exception (even one thrown
        // before its first await) is captured in the discarded task; RunReapplyAsync's own catch logs it (IN-15).
        _ = mode
            ? RunReapplyAsync("settings", force: true)
            : RunReapplyAsync("display", force: false);
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
        }

        Log.Info($"settings applied interval={_appliedIntervalMinutes} mode={_settings.Mode} resolution={_settings.Resolution} market={_settings.Market} monitors={_settings.MonitorMode} language={_settings.Language} next={ReadNextDue()?.ToString("O") ?? "-"}");
        Nudge("settings");
        RaiseStateChanged();

        // A monitor-mode switch is visible within seconds, not at the next tick (criterion 2 "applies immediately"):
        // per-monitor -> the plan over the current image; same -> the current image on every monitor. Forced, so the
        // attached-set comparison does not suppress it. Nothing is committed here: _appliedMonitorMode moves only
        // when an apply succeeds (WR-01). A busy gate defers the switch to the holder's release
        // (ReapplyIfPending); a failure or an empty desktop leaves the setting pending for the next
        // ApplySettings / applying tick. Started LAST: the re-apply's own finally raises after its release, so the
        // raise above reflects the schedule only and the window never observes a transient gate hold that nothing
        // clears (CR-01).
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

        // The timer reference is taken under _sync, where Dispose also nulls it (WR-03): a signal that passed the
        // _disposed check above can no longer dereference a field Dispose has just cleared. The Change call itself
        // stays outside the lock; if Dispose won the race it throws ObjectDisposedException, handled below.
        ITimer timer;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            timer = _displayTimer ??= _time.CreateTimer(_ => OnDisplayDebounce(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        try
        {
            timer.Change(ScheduleMath.DisplayChangeDebounce, Timeout.InfiniteTimeSpan);
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

    /// <summary>
    /// Test/diagnostic helper: completes once no tick holds the gate. It briefly owns the gate itself, so — like
    /// every other holder — it runs <see cref="ReapplyIfPending"/> after its release: a re-apply whose
    /// <c>Wait(0)</c> landed inside that window is dispatched now instead of waiting for the next holder (IN-12).
    /// </summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _gate.Release();
        ReapplyIfPending();
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

        // Detach the display timer under _sync — the same lock OnDisplayChanged reads it under (WR-03) — and dispose
        // it outside, so a debounce callback that is taking _sync right now cannot wait on the dispose.
        ITimer? displayTimer;
        lock (_sync)
        {
            displayTimer = _displayTimer;
            _displayTimer = null;
        }

        displayTimer?.Dispose();
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
            // 1. fetch — one conditional GET per tick (D-02); the full list (newest first) is read because step 4b
            // backfills from the same rows (CACHE-06); an empty list means neither source produced a row (Pitfall 5).
            IReadOnlyList<CatalogEntry> rows = await _catalog.GetCatalogAsync(_settings.Market, ct).ConfigureAwait(false);
            CatalogEntry? entry = rows.Count > 0 ? rows[0] : null;
            if (entry is null)
            {
                Log.Warn("pipeline failed stage=catalog error=no catalog source produced rows");
            }

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
            // The monitor mode is read ONCE here: the same value decides the branch and is committed as the applied
            // mode after a successful apply (WR-01), never re-read from the shared Settings in between.
            if (path is not null)
            {
                string applyPath = path;
                ImageId applyId = id;
                string mode = _settings.MonitorMode;
                if (string.Equals(mode, Settings.PerMonitorMode, StringComparison.Ordinal))
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

                if (apply.Ok)
                {
                    lock (_sync)
                    {
                        _appliedMonitorMode = mode;   // commit-on-success: the desktop now reflects this mode (WR-01)
                    }
                }

                result = apply.Ok ? TickResult.Applied : TickResult.ApplyFailed;
            }

            // 4b. backfill (CACHE-06) — after the apply, never before it; only on a tick whose fetch and newest ensure
            // both succeeded (never offline); at most one download. A throw anywhere above skips this line entirely
            // (it lands in the catch below), so `threw` needs no test here. Its own try/catch inside BackfillOneAsync
            // keeps a backfill failure out of the ladder, the schedule and the tick result.
            if (!fetchFailed)
            {
                await BackfillOneAsync(rows, resolution, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Tick step 4b (CACHE-06): while the cache holds fewer than <see cref="ImageCache.MaxImages"/> validated images,
    /// download exactly one older catalog image so that "Next" and random mode have material within days of install.
    /// Runs only from a successful tick — one whose fetch produced rows and whose newest ensure did not fail — and
    /// always after the apply, so the newest-image step is never delayed or replaced. The candidate is the newest row
    /// (rows are newest first) whose ID is cached at no resolution at all, falling back to the newest row that is not
    /// cached with its file on disk for this tick's resolution (so a resolution change does not spend the cap on the
    /// same days twice, IN-03); rows in the process-local <see cref="_backfillSkip"/> set are never candidates, and
    /// a rejected download retires its ID for the rest of the process (a restart retries once) so one dead catalog ID
    /// never costs a request on every tick. <paramref name="resolution"/>
    /// is the tick-local effective resolution (Auto never reaches here), so the backfill goes through the same
    /// <see cref="ImageCache.EnsureAsync"/> pipeline as the newest image: host allow-list, JPEG validation, dimension
    /// check and the 64 MB cap are unchanged. The cap check precedes any download because <see cref="ImageCache.Add"/>
    /// evicts down to the cap and would otherwise churn the oldest image. Any failure only logs — it never writes the
    /// tick flags, the D-13 ladder, the schedule or the result.
    /// </summary>
    private async Task BackfillOneAsync(IReadOnlyList<CatalogEntry> rows, string resolution, CancellationToken ct)
    {
        try
        {
            if (_cache.Index.Images.Count >= ImageCache.MaxImages)
            {
                Log.Info("backfill skipped cause=full");
                return;
            }

            // Two passes, both newest first (IN-03): cache identity is (id, resolution) and the cap counts entries,
            // so after a resolution change the plain "not cached at this resolution" rule would re-download the
            // days already held at the old resolution and fill the cap with two variants of the same five days.
            // Pass 1 therefore prefers IDs absent at every resolution (new days for the cache); pass 2 is the
            // original rule, reached only once every catalog row is held at some resolution.
            bool NotCachedHere(CatalogEntry r) =>
                !_backfillSkip.Contains(r.Id.Value)
                && (_cache.TryGet(r.Id, resolution) is not { } hit || !FileExists(hit));
            bool CachedAnywhere(CatalogEntry r) =>
                _cache.Index.Images.Any(i => string.Equals(i.Id, r.Id.Value, StringComparison.Ordinal) && FileExists(i));

            CatalogEntry? candidate = rows.FirstOrDefault(r => NotCachedHere(r) && !CachedAnywhere(r))
                ?? rows.FirstOrDefault(NotCachedHere);
            if (candidate is null)
            {
                Log.Info("backfill skipped cause=no-candidate");
                return;
            }

            CachedImage? added = await _cache.EnsureAsync(candidate, resolution, _http, ct).ConfigureAwait(false);
            if (added is null)
            {
                _backfillSkip.Add(candidate.Id.Value);
                Log.Warn($"backfill failed id={candidate.Id}");
                return;
            }

            Log.Info($"backfill id={added.Id} file={added.File} cached={_cache.Index.Images.Count}/{ImageCache.MaxImages}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // only ct decides what counts as cancellation (WR-04); the tick's own catch rethrows it
        }
        catch (Exception ex)
        {
            Log.Warn($"backfill failed error={ex.Message}", ex);   // no flag writes: the ladder, schedule and result are untouched
        }
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
        if (result.Ok)
        {
            // Commit-on-success, like _appliedMonitorMode (WR-01): a failed apply leaves the previous set in place so
            // the next display signal with the same monitors is retried instead of skipped as unchanged (WR-04).
            _lastAttachedSet = DeviceSet(monitors);
        }

        return result;
    }

    /// <summary>The attached monitors as a sorted device-path array (order-independent comparison of two enumerations).</summary>
    private static string[] DeviceSet(IReadOnlyList<MonitorHandle> monitors) =>
        monitors.Select(m => m.DevicePath).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    // ---- re-apply (WALL-03 dock/undock, mode switch) ----------------------------------------------------

    private void OnDisplayDebounce()
    {
        // Timer callback on a thread-pool thread: nothing may escape (WR-02). Async method: any exception (even one
        // thrown before its first await) is captured in the discarded task; RunReapplyAsync's own catch logs it (IN-15).
        _ = RunReapplyAsync("display", force: false);
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
    /// Returns true only when an apply ran and reported Ok — and only then is the monitor mode it applied in committed
    /// to <c>_appliedMonitorMode</c> (WR-01). Either kind marks itself pending before its gate attempt: when the
    /// gate is busy the holder's finally runs it after its release (<see cref="ReapplyIfPending"/>, WR-01 / WR-05);
    /// when this call owns the gate it clears its flag, so its own failure does not re-dispatch itself (T-03-22). A
    /// concurrent forced call can still have re-set the flag in between, in which case the finally runs at most one
    /// bounded follow-up — never a loop (IN-11).
    /// </summary>
    private async Task<bool> RunReapplyAsync(string reason, bool force)
    {
        if (_disposed)
        {
            return false;
        }

        if (!force && !_settings.IsPerMonitor)
        {
            Log.Info($"reapply skipped reason={reason} cause=same-mode");
            return false;
        }

        lock (_sync)
        {
            // BEFORE the gate attempt: a holder releasing in between still sees it (WR-01 for the mode switch, WR-05
            // for the display change) — the flag for the reason this call serves.
            if (force)
            {
                _modeReapplyPending = true;
            }
            else
            {
                _displayReapplyPending = true;
            }
        }

        if (!_gate.Wait(0, CancellationToken.None))
        {
            Log.Info($"reapply skipped reason={reason} cause=busy");   // the gate holder's finally re-checks the pending re-apply (ReapplyIfPending) after its release
            return false;
        }

        bool applied = false;
        try
        {
            lock (_sync)
            {
                // This call IS the follow-up for its reason: its own failure will not re-dispatch it. (A concurrent
                // forced call may re-set the flag after this point — at most one bounded extra attempt, IN-11.)
                if (force)
                {
                    _modeReapplyPending = false;
                }
                else
                {
                    _displayReapplyPending = false;
                }
            }

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
                return false;
            }

            string primaryPath = Path.GetFullPath(Path.Combine(_cache.CacheDir, current.File));
            (ApplyResult? apply, int monitorCount, string appliedMode) = await _ui.InvokeAsync(() =>
            {
                string mode = _settings.MonitorMode;   // read once: decides the branch and is the value committed below
                IReadOnlyList<MonitorHandle> monitors = _applier.GetAttachedMonitors();
                string[] set = DeviceSet(monitors);
                if (!force && set.SequenceEqual(_lastAttachedSet, StringComparer.Ordinal))
                {
                    Log.Info($"reapply skipped reason={reason} cause=unchanged");
                    return ((ApplyResult?)null, monitors.Count, mode);
                }

                ApplyResult result;
                if (string.Equals(mode, Settings.PerMonitorMode, StringComparison.Ordinal))
                {
                    result = ApplyPlan(monitors, candidates, primaryId, primaryPath, appliedUtc);
                }
                else
                {
                    result = ApplyStage.Run(_applier, primaryPath, primaryId, _cache, _state, _statePath, appliedUtc);
                    if (result.Ok)
                    {
                        _lastAttachedSet = set;   // commit-on-success (WR-04); a failed apply keeps the set retryable
                    }
                }

                return (result, monitors.Count, mode);
            }, _ct).ConfigureAwait(false);

            if (apply is not null)
            {
                Log.Info($"reapply reason={reason} monitors={monitorCount} result={(apply.Ok ? "Applied" : "Failed")}");
            }

            if (apply is { Ok: true })
            {
                string mode = appliedMode;
                lock (_sync)
                {
                    _appliedMonitorMode = mode;   // commit-on-success: the desktop now reflects this mode (WR-01)
                }

                applied = true;
            }
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            // Shutdown cancelled the UI hop: not a failure, the desktop is left as it is (IN-10).
            Log.Info($"reapply cancelled reason={reason}");
        }
        catch (Exception ex)
        {
            Log.Warn($"reapply failed reason={reason} error={ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
            RaiseStateChanged();     // after the release: a subscriber's Snapshot() must read TickRunning == false (CR-01)
            ReapplyIfPending();      // a switch or display change that met THIS re-apply's gate hold runs now
        }

        return applied;
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
