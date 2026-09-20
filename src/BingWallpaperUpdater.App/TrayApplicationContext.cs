using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;
using BingWallpaperUpdater.Core.Pipeline;
using BingWallpaperUpdater.Core.Ports;
using BingWallpaperUpdater.Windows.Wallpaper;
using Microsoft.Win32;

namespace BingWallpaperUpdater.App;

/// <summary>
/// The tray shell: one <see cref="NotifyIcon"/> with an Exit item, no window ever. The launch-time pipeline
/// runs on the thread pool and only the COM apply hops back to this (STA) thread via the captured
/// <see cref="SynchronizationContext"/>. Every exit path (Exit menu, thread/unhandled exception, session
/// ending) funnels through <see cref="Shutdown"/>, which logs once, hides and disposes the icon, then ends
/// the message loop. <see cref="Dispose(bool)"/> is idempotent because WinForms disposes the context again
/// when the loop ends (RESEARCH Pitfall 4).
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly CancellationTokenSource _cts = new();
    private readonly SessionEndingEventHandler _sessionEnding;
    private int _shutdownRequested;
    private bool _disposed;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => Shutdown("exit"));

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

        _ = Task.Run(() => RunPipelineAsync(ui, _cts.Token));
    }

    private static async Task RunPipelineAsync(SynchronizationContext ui, CancellationToken ct)
    {
        try
        {
            using var http = new HttpGateway();
            Settings settings = Settings.LoadOrCreate(AppPaths.SettingsPath);
            AppState state = AppState.LoadOrCreate(AppPaths.StatePath);
            var cache = new ImageCache(AppPaths.CacheDir, AppPaths.IndexPath);
            cache.Load();
            var catalog = new CatalogService(http, state, AppPaths.CatalogBodyPath);

            (string AbsolutePath, ImageId Id)? result =
                await FirstRunPipeline.EnsureTodayAsync(settings, state, catalog, cache, http, ct).ConfigureAwait(false);

            // Persist the catalog ETag/fetch time whether or not a download followed.
            state.Save(AppPaths.StatePath);

            if (result is null)
            {
                return; // already logged; the desktop is left untouched
            }

            ui.Post(_ => ApplyOnUiThread(result.Value.AbsolutePath, result.Value.Id, cache, state), null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"pipeline failed stage=download error={ex.Message}", ex);
        }
    }

    /// <summary>
    /// Runs <see cref="ApplyStage"/> on the STA thread: the index protection is persisted before the desktop
    /// changes, the record is rolled back on a failed apply, and <c>apply ok</c> is logged only once everything is
    /// on disk (WR-07). Failure and rollback logging live in the stage; this only owns the adapter and the catch.
    /// </summary>
    private static void ApplyOnUiThread(string absolutePath, ImageId id, ImageCache cache, AppState state)
    {
        try
        {
            IWallpaperApplier applier = new DesktopWallpaperApplier();
            ApplyStage.Run(applier, absolutePath, id, cache, state, AppPaths.StatePath);
        }
        catch (Exception ex)
        {
            Log.Warn($"pipeline failed stage=apply error={ex.Message}", ex);
        }
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
            _icon.Visible = false;
            _icon.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
