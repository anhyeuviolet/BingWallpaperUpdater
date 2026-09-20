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
        if (!createdNew)
        {
            return 0;
        }

        AppPaths.EnsureDirectories();
        Log.Initialize(AppPaths.LogPath);

        ApplicationConfiguration.Initialize();
        Log.Info($"startup version={InformationalVersion()} pid={Environment.ProcessId}");
        Application.Run(new TrayApplicationContext());
        return 0;
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
