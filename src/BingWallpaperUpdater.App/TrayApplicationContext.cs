using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;
using BingWallpaperUpdater.Core.Ports;
using BingWallpaperUpdater.Core.Rotation;
using BingWallpaperUpdater.Core.Scheduling;
using BingWallpaperUpdater.Windows.Wallpaper;
using Microsoft.Win32;

namespace BingWallpaperUpdater.App;

/// <summary>
/// The Phase 2 tray shell: one <see cref="NotifyIcon"/> with "Next wallpaper" / separator / "Exit" (D-15) and no
/// visible window ever. It owns the process-lifetime services and the <see cref="RotationService"/> whose 60 s
/// heartbeat drives every scheduled tick on the thread pool; only the COM apply hops back to this (STA) thread
/// through <see cref="WinFormsUiDispatcher"/>, and the Next item is the only place outside the service that starts
/// a tick.
/// Signals never run a tick, they only re-arm the heartbeat (D-10, ROT-04): the hidden <see cref="PowerWindow"/>
/// turns <c>PBT_APMRESUMEAUTOMATIC</c> / display-on into <see cref="RotationService.Nudge"/> (8 s debounce), and the
/// secondary bridges map <see cref="SystemEvents.TimeChanged"/> to <see cref="RotationService.OnClockChanged"/>,
/// <see cref="SystemEvents.PowerModeChanged"/> (Resume) to a nudge, <see cref="NetworkChange.NetworkAvailabilityChanged"/>
/// to <see cref="RotationService.OnNetworkAvailable"/>, and <see cref="SystemEvents.DisplaySettingsChanged"/> to a log
/// line (per-monitor re-apply is Phase 3). Those handlers run on system-events / thread-pool threads and touch
/// nothing but the thread-safe service methods and <see cref="Log"/>.
/// Every exit path (Exit menu, thread/unhandled exception, session ending) funnels through <see cref="Shutdown"/>,
/// which logs once and disposes in a fixed order: unsubscribe the static events, cancel the token, stop the
/// heartbeat, unregister and destroy the power window, hide and dispose the icon, dispose the gateway, then the
/// token source; an in-flight tick is not awaited (its state/index writes are atomic; process exit ends it).
/// <see cref="Dispose(bool)"/> is idempotent because WinForms disposes the context again when the loop ends.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly CancellationTokenSource _cts = new();
    private readonly SessionEndingEventHandler _sessionEnding;
    private readonly EventHandler _timeChanged;
    private readonly PowerModeChangedEventHandler _powerModeChanged;
    private readonly NetworkAvailabilityChangedEventHandler _networkChanged;
    private readonly EventHandler _displayChanged;
    private readonly HttpGateway _http;
    private readonly RotationService _rotation;
    private readonly PowerWindow _powerWindow;
    private int _shutdownRequested;
    private bool _disposed;

    /// <param name="startup">True when launched with <c>--startup</c>: the first tick waits 30-60 s (D-12).</param>
    public TrayApplicationContext(bool startup)
    {
        // The menu and icon come first: creating the first WinForms control is what installs the
        // WindowsFormsSynchronizationContext that the dispatcher captures below.
        var menu = new ContextMenuStrip();
        var next = new ToolStripMenuItem("Next wallpaper");
        next.Click += (_, _) => _ = _rotation.RunTickAsync(TickReason.Next, _cts.Token);
        menu.Items.Add(next);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown("exit"));
        menu.Opening += (_, _) => next.Enabled = !_rotation.IsTickRunning;   // D-05: disabled while a tick runs

        _icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Bing Wallpaper Updater",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _sessionEnding = (_, _) => Shutdown("session-ending");
        SystemEvents.SessionEnding += _sessionEnding;

        SynchronizationContext ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("no WinForms synchronization context on the UI thread");

        _http = new HttpGateway();
        Settings settings = Settings.LoadOrCreate(AppPaths.SettingsPath);
        AppState state = AppState.LoadOrCreate(AppPaths.StatePath);
        var cache = new ImageCache(AppPaths.CacheDir, AppPaths.IndexPath);
        cache.Reconcile();   // CACHE-05: once per launch, before the first tick
        var catalog = new CatalogService(_http, state, AppPaths.CatalogBodyPath);
        IWallpaperApplier applier = new DesktopWallpaperApplier();
        var dispatcher = new WinFormsUiDispatcher(ui);
        _rotation = new RotationService(settings, state, AppPaths.StatePath, catalog, cache, _http, applier, dispatcher);

        // D-10 resume fast path: the hidden window only re-arms the heartbeat (8 s debounce); the heartbeat runs the check.
        _powerWindow = new PowerWindow();
        _powerWindow.Resumed += source => _rotation.Nudge(source);

        // Secondary bridges (RESEARCH Pattern 6). Each handler lives in a field so Dispose can unsubscribe it; each
        // only calls a thread-safe RotationService method or Log — never the icon or the menu.
        _timeChanged = (_, _) => _rotation.OnClockChanged();                                          // D-07 clamp + nudge
        _powerModeChanged = (_, e) => { if (e.Mode == PowerModes.Resume) _rotation.Nudge("power-mode-changed"); };   // secondary only (dotnet/runtime #123773)
        _networkChanged = (_, e) => { if (e.IsAvailable) _rotation.OnNetworkAvailable(); };            // ends a retry wait early
        _displayChanged = (_, _) => Log.Info("display settings changed");                             // log only; WALL-03 is Phase 3
        SystemEvents.TimeChanged += _timeChanged;
        SystemEvents.PowerModeChanged += _powerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += _networkChanged;
        SystemEvents.DisplaySettingsChanged += _displayChanged;

        TimeSpan initialDelay = startup ? TimeSpan.FromSeconds(Random.Shared.Next(30, 61)) : TimeSpan.Zero;
        Log.Info($"schedule start launch={(startup ? "autostart" : "manual")} firstTickIn={(int)initialDelay.TotalSeconds}s interval={settings.IntervalMinutes} mode={settings.Mode}");
        _rotation.Start(initialDelay, _cts.Token);
    }

    private static Icon LoadTrayIcon()
    {
        using Stream? stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("tray.ico");
        return stream is null
            ? SystemIcons.Application
            : new Icon(stream, SystemInformation.SmallIconSize);
    }

    /// <summary>The single exit path. Safe to call more than once and from any route.</summary>
    public void Shutdown(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return;
        }

        Log.Info($"shutdown reason={reason}");
        Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            // Order matters (Pitfall 9): no new signals -> no new ticks -> no power callbacks -> icon -> sockets -> token.
            SystemEvents.SessionEnding -= _sessionEnding;
            SystemEvents.TimeChanged -= _timeChanged;
            SystemEvents.PowerModeChanged -= _powerModeChanged;
            SystemEvents.DisplaySettingsChanged -= _displayChanged;
            NetworkChange.NetworkAvailabilityChanged -= _networkChanged;
            _cts.Cancel();
            _rotation.Dispose();
            _powerWindow.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _http.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
