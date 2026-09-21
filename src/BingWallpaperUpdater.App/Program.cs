using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Autostart;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Windows.Autostart;

namespace BingWallpaperUpdater.App;

/// <summary>Entry point. The supported floor is Windows 10 1809; the attribute silences CA1416 for IDesktopWallpaper (Windows 8+).</summary>
[SupportedOSPlatform("windows8.0")]
internal static class Program
{
    /// <summary>Per-session single-instance guard; Phase 4's installer uses the same name as AppMutex.</summary>
    private const string MutexName = @"Local\BingWallpaperUpdater";

    /// <summary>
    /// "Show Settings" signal, session-scoped like the mutex (UI-06): a second launch sets it and exits, the primary
    /// instance waits on it and opens or activates the one Settings window. No HWND broadcast, no pipe.
    /// </summary>
    private const string ShowEventName = @"Local\BingWallpaperUpdater.Show";

    /// <summary>Autostart flag in the HKCU Run value (D-12, INST-02); the parser and the producer share one literal. Matched case-insensitively, no value.</summary>
    private const string StartupFlag = AutostartCommand.Flag;

    [STAThread]
    private static int Main(string[] args)
    {
        // A second launch must exit immediately: no log line, no network, no cache access (SRC-09). Its only effect
        // is the Show signal to the primary (UI-06).
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew && !TryAcquireExisting(mutex))
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle? show))
            {
                using (show)
                {
                    show.Set();
                }
            }

            return 0;
        }

        // Created right after the mutex (Pitfall 12) so a second launch during startup finds it; owned here for the
        // process lifetime — the context registers a wait on it but never disposes it.
        using var showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowEventName, out _);

        AppPaths.EnsureDirectories();
        Log.Initialize(AppPaths.LogPath);

        // Settings and the UI culture come before the first WinForms control (Pitfall 4): the tray menu is built in
        // the context constructor and must already read the right resource set.
        Settings settings = Settings.LoadOrCreate(AppPaths.SettingsPath);

        // INST-02: when autostart is wanted, refresh the quoted absolute-path Run value on every start so an upgrade
        // or a moved folder self-heals. Deliberately leaves StartupApproved alone — a "Disabled" the user set in
        // Task Manager wins over a manual launch (RESEARCH Pitfall 9); only the Settings checkbox writes it.
        if (settings.Autostart && Environment.ProcessPath is { Length: > 0 } exe)
        {
            RegistryAutostartManager.RewriteRunValue(exe);
        }

        UiCulture.Apply(settings.Language);

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);   // .NET 10 stable API; Windows 11 only, light on Windows 10
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        TrayApplicationContext? context = null;
        Application.ThreadException += (_, e) =>
        {
            Log.Warn("unhandled", e.Exception);
            context?.Shutdown("unhandled");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Warn("unhandled", e.ExceptionObject as Exception);
            context?.Shutdown("unhandled");
        };

        // --startup delays the first tick 30-60 s to dodge logon network races (D-12); everything else ticks at once.
        bool startup = args.Any(a => string.Equals(a, StartupFlag, StringComparison.OrdinalIgnoreCase));

        Log.Info($"startup version={InformationalVersion()} pid={Environment.ProcessId}");
        context = new TrayApplicationContext(settings, showEvent, startup);
        Application.Run(context);
        return 0;
    }

    /// <summary>
    /// The mutex already existed. If its previous owner died without releasing it, the wait surfaces an
    /// <see cref="AbandonedMutexException"/> — which means acquisition SUCCEEDED (PITFALLS P10), so a crash
    /// never blocks the next start. A live owner makes the zero-timeout wait return false.
    /// </summary>
    private static bool TryAcquireExisting(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static string InformationalVersion()
    {
        string v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).ProductVersion
            ?? "0.0";
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }
}
