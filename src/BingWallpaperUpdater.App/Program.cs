using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;

namespace BingWallpaperUpdater.App;

/// <summary>Entry point. The supported floor is Windows 10 1809; the attribute silences CA1416 for IDesktopWallpaper (Windows 8+).</summary>
[SupportedOSPlatform("windows8.0")]
internal static class Program
{
    /// <summary>Per-session single-instance guard; Phase 4's installer uses the same name as AppMutex.</summary>
    private const string MutexName = @"Local\BingWallpaperUpdater";

    [STAThread]
    private static int Main()
    {
        // A second launch must exit immediately: no log line, no network, no cache access (SRC-09).
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew && !TryAcquireExisting(mutex))
        {
            return 0;
        }

        AppPaths.EnsureDirectories();
        Log.Initialize(AppPaths.LogPath);

        ApplicationConfiguration.Initialize();
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

        Log.Info($"startup version={InformationalVersion()} pid={Environment.ProcessId}");
        context = new TrayApplicationContext();
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
