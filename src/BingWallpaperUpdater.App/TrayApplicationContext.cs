using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using BingWallpaperUpdater.App.Resources;
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
/// The Phase 3 tray shell: one <see cref="NotifyIcon"/> with a localized "Next wallpaper" / "Settings" / separator /
/// "Exit" menu (D-15, L10N-01) and at most one visible window — the <see cref="SettingsForm"/>, created on demand by
/// <see cref="ShowSettings"/> from three routes (left-click on the icon, the Settings item, a second launch signalling
/// the <c>Local\BingWallpaperUpdater.Show</c> event) and disposed on close (UI-01, UI-02, UI-06). Closing the window
/// never ends the message loop because <see cref="ApplicationContext.MainForm"/> stays null; only <see cref="Shutdown"/>
/// calls <see cref="ApplicationContext.ExitThread"/>.
/// It owns the process-lifetime services and the <see cref="RotationService"/> whose 60 s heartbeat drives every
/// scheduled tick on the thread pool; only the COM apply hops back to this (STA) thread through
/// <see cref="WinFormsUiDispatcher"/>, and the Next item / Next button are the only places outside the service that
/// start a tick. The shared <see cref="Settings"/> instance is loaded by <c>Main</c> (after which the UI culture is
/// applied, Pitfall 4) and handed to both the service and the window (D-09).
/// Signals never run a tick, they only re-arm the heartbeat (D-10, ROT-04): the hidden <see cref="PowerWindow"/>
/// turns <c>PBT_APMRESUMEAUTOMATIC</c> / display-on into <see cref="RotationService.Nudge"/> (8 s debounce), and the
/// secondary bridges map <see cref="SystemEvents.TimeChanged"/> to <see cref="RotationService.OnClockChanged"/>,
/// <see cref="SystemEvents.PowerModeChanged"/> (Resume) to a nudge, <see cref="NetworkChange.NetworkAvailabilityChanged"/>
/// to <see cref="RotationService.OnNetworkAvailable"/>, and <see cref="SystemEvents.DisplaySettingsChanged"/> to a log
/// line (per-monitor re-apply is Plan 03-04). Those handlers run on system-events / thread-pool threads and touch
/// nothing but the thread-safe service methods and <see cref="Log"/>; the Show-event wait posts to the UI thread.
/// Every exit path (Exit menu, thread/unhandled exception, session ending) funnels through <see cref="Shutdown"/>,
/// which logs once and disposes in a fixed order: unsubscribe the static events and the Show wait, cancel the token,
/// stop the heartbeat, unregister and destroy the power window, dispose the window, hide and dispose the icon,
/// dispose the gateway, then the token source; an in-flight tick is not awaited (its state/index writes are atomic;
/// process exit ends it). <see cref="Dispose(bool)"/> is idempotent because WinForms disposes the context again when
/// the loop ends.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Settings _settings;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _nextItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly CancellationTokenSource _cts = new();
    private readonly SessionEndingEventHandler _sessionEnding;
    private readonly EventHandler _timeChanged;
    private readonly PowerModeChangedEventHandler _powerModeChanged;
    private readonly NetworkAvailabilityChangedEventHandler _networkChanged;
    private readonly EventHandler _displayChanged;
    private readonly HttpGateway _http;
    private readonly RotationService _rotation;
    private readonly PowerWindow _powerWindow;
    private readonly RegisteredWaitHandle? _showWait;
    private SettingsForm? _settingsForm;
    private int _shutdownRequested;
    private bool _disposed;

    /// <param name="settings">The instance loaded by <c>Main</c>; shared with the service and the window (D-09).</param>
    /// <param name="showEvent">The <c>Local\BingWallpaperUpdater.Show</c> event owned by <c>Main</c>; a set opens or activates Settings.</param>
    /// <param name="startup">True when launched with <c>--startup</c>: the first tick waits 30-60 s (D-12).</param>
    public TrayApplicationContext(Settings settings, EventWaitHandle showEvent, bool startup)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(showEvent);
        _settings = settings;

        // The menu and icon come first: creating the first WinForms control is what installs the
        // WindowsFormsSynchronizationContext that the dispatcher captures below. Handlers that touch _rotation are
        // attached only after it is assigned (Pitfall 11: the deferred CS8602 warnings).
        var menu = new ContextMenuStrip();
        _nextItem = new ToolStripMenuItem(Strings.Get("Menu_Next"));
        _settingsItem = new ToolStripMenuItem(Strings.Get("Menu_Settings"));
        _exitItem = new ToolStripMenuItem(Strings.Get("Menu_Exit"));
        _exitItem.Click += (_, _) => Shutdown("exit");
        menu.Items.Add(_nextItem);
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        // Shown only once everything below has succeeded (IN-02): a constructor that throws after the icon is
        // visible leaves it in the tray until hovered (dotnet/winforms #6996) and nothing would ever dispose it.
        _icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = Strings.Get("Tray_Tooltip"),
            ContextMenuStrip = menu,
            Visible = false,
        };

        SynchronizationContext ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("no WinForms synchronization context on the UI thread");

        _http = new HttpGateway();
        RotationService? rotation = null;
        PowerWindow? powerWindow = null;
        try
        {
            AppState state = AppState.LoadOrCreate(AppPaths.StatePath);
            var cache = new ImageCache(AppPaths.CacheDir, AppPaths.IndexPath);
            cache.Reconcile();   // CACHE-05: once per launch, before the first tick
            var catalog = new CatalogService(_http, state, AppPaths.CatalogBodyPath);
            IWallpaperApplier applier = new DesktopWallpaperApplier();
            var dispatcher = new WinFormsUiDispatcher(ui);
            rotation = new RotationService(settings, state, AppPaths.StatePath, catalog, cache, _http, applier, dispatcher);

            // D-10 resume fast path: the hidden window only re-arms the heartbeat (8 s debounce); the heartbeat runs the check.
            powerWindow = new PowerWindow();
        }
        catch
        {
            // Nothing is visible or subscribed yet; release what was created so a failed launch leaves no tray
            // icon, no power window and no open socket behind, then let the exception reach Main.
            powerWindow?.Dispose();
            rotation?.Dispose();
            _http.Dispose();
            _icon.Dispose();
            _cts.Dispose();
            throw;
        }

        _rotation = rotation;
        _powerWindow = powerWindow;
        _powerWindow.Resumed += source => _rotation.Nudge(source);

        // Menu and icon handlers that need the service — attached after _rotation exists so no lambda captures an
        // unassigned readonly field.
        _nextItem.Click += (_, _) => _ = _rotation.RunTickAsync(TickReason.Next, _cts.Token);
        menu.Opening += (_, _) => _nextItem.Enabled = !_rotation.IsTickRunning;   // D-05: disabled while a tick runs
        _settingsItem.Click += (_, _) => ShowSettings();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettings();
            }
        };

        // UI-06: a second launch sets the event from its own process; the wait callback runs on a thread-pool thread
        // and posts ShowSettings to the UI thread. The event is owned by Main and never disposed here.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ui.Post(_ => ShowSettings(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        // Secondary bridges (RESEARCH Pattern 6). Each handler lives in a field so Dispose can unsubscribe it; each
        // only calls a thread-safe RotationService method or Log — never the icon or the menu.
        _timeChanged = (_, _) => _rotation.OnClockChanged();                                          // D-07 clamp + nudge
        _powerModeChanged = (_, e) => { if (e.Mode == PowerModes.Resume) _rotation.Nudge("power-mode-changed"); };   // secondary only (dotnet/runtime #123773)
        _networkChanged = (_, e) => { if (e.IsAvailable) _rotation.OnNetworkAvailable(); };            // ends a retry wait early
        _displayChanged = (_, _) => Log.Info("display settings changed");                             // log only; WALL-03 is Plan 03-04
        SystemEvents.TimeChanged += _timeChanged;
        SystemEvents.PowerModeChanged += _powerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += _networkChanged;
        SystemEvents.DisplaySettingsChanged += _displayChanged;

        // Last: everything Shutdown disposes now exists, so the icon may appear and session end may route to it.
        _sessionEnding = (_, _) => Shutdown("session-ending");
        SystemEvents.SessionEnding += _sessionEnding;
        _icon.Visible = true;

        TimeSpan initialDelay = startup ? TimeSpan.FromSeconds(Random.Shared.Next(30, 61)) : TimeSpan.Zero;
        Log.Info($"schedule start launch={(startup ? "autostart" : "manual")} firstTickIn={(int)initialDelay.TotalSeconds}s interval={_settings.IntervalMinutes} mode={_settings.Mode} resolution={_settings.Resolution} market={_settings.Market} monitors={_settings.MonitorMode} language={_settings.Language}");
        _rotation.Start(initialDelay, _cts.Token);
    }

    private static Icon LoadTrayIcon()
    {
        using Stream? stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("tray.ico");
        return stream is null
            ? SystemIcons.Application
            : new Icon(stream, SystemInformation.SmallIconSize);
    }

    /// <summary>
    /// UI thread only: creates the single <see cref="SettingsForm"/> or activates the existing one (UI-01, UI-06).
    /// <c>FormClosed</c> nulls the field — <c>Close()</c> on a shown form disposes it, so nothing invisible stays
    /// alive (UI-02). Reached from the Settings item, a left-click, the Show-event post and the language switch.
    /// </summary>
    public void ShowSettings()
    {
        if (_disposed)
        {
            return;
        }

        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
            {
                _settingsForm.WindowState = FormWindowState.Normal;
            }

            _settingsForm.Activate();
            Log.Info("settings window action=activate");
            return;
        }

        _settingsForm = new SettingsForm(_settings, _rotation, this, _cts.Token);
        _settingsForm.FormClosed += (_, e) =>
        {
            _settingsForm = null;
            Log.Info($"settings window action=close reason={e.CloseReason}");
        };
        _settingsForm.Show();
        Log.Info("settings window action=open");
    }

    /// <summary>UI thread only: re-reads the menu and tooltip strings under the current UI culture (L10N-03).</summary>
    public void RetextMenu()
    {
        if (_disposed)
        {
            return;
        }

        _nextItem.Text = Strings.Get("Menu_Next");
        _settingsItem.Text = Strings.Get("Menu_Settings");
        _exitItem.Text = Strings.Get("Menu_Exit");
        _icon.Text = Strings.Get("Tray_Tooltip");
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
            // Order matters (Pitfall 9): no new signals -> no new ticks -> no power callbacks -> window -> icon -> sockets -> token.
            SystemEvents.SessionEnding -= _sessionEnding;
            SystemEvents.TimeChanged -= _timeChanged;
            SystemEvents.PowerModeChanged -= _powerModeChanged;
            SystemEvents.DisplaySettingsChanged -= _displayChanged;
            NetworkChange.NetworkAvailabilityChanged -= _networkChanged;
            _showWait?.Unregister(null);
            _cts.Cancel();
            _rotation.Dispose();
            _powerWindow.Dispose();
            _settingsForm?.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _http.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
