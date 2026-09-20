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
/// The tray shell: one <see cref="NotifyIcon"/> with "Next wallpaper" / separator / "Exit" (D-15), no window ever.
/// It owns the process-lifetime services and the <see cref="RotationService"/> whose heartbeat drives every tick on
/// the thread pool; only the COM apply hops back to this (STA) thread through <see cref="WinFormsUiDispatcher"/>.
/// Every exit path (Exit menu, thread/unhandled exception, session ending) funnels through <see cref="Shutdown"/>,
/// which logs once, stops the heartbeat, hides and disposes the icon, then ends the message loop.
/// <see cref="Dispose(bool)"/> is idempotent because WinForms disposes the context again when the loop ends
/// (RESEARCH Pitfall 4).
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly CancellationTokenSource _cts = new();
    private readonly SessionEndingEventHandler _sessionEnding;
    private readonly HttpGateway _http;
    private readonly RotationService _rotation;
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
            SystemEvents.SessionEnding -= _sessionEnding;
            _cts.Cancel();
            _rotation.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _http.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
