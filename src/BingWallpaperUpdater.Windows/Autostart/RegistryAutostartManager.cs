using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Autostart;
using BingWallpaperUpdater.Core.Diagnostics;
using Microsoft.Win32;

namespace BingWallpaperUpdater.Windows.Autostart;

/// <summary>
/// Per-user autostart over the two HKCU keys Task Manager's Startup tab reads (INST-02; CLAUDE.md "Per-user
/// autostart"): the <c>Run</c> value (what Explorer executes at logon) and the <c>StartupApproved\Run</c> value (the
/// enabled / disabled flag the user can flip in Task Manager). Only <see cref="Registry.CurrentUser"/> is ever
/// opened and only <see cref="ValueName"/> is ever read, written or deleted — never HKLM, never another product's
/// entry (Microsoft's own <c>BingWallpaperApp</c> value lives next to ours), never the Task Scheduler or the Startup
/// folder, so nothing here needs elevation (T-03-14, T-03-16).
/// <para>
/// Observed vs desired state: <see cref="IsEnabled"/> is what Task Manager shows; <c>Settings.Autostart</c> is what
/// the user asked for. <see cref="RewriteRunValue"/> runs on every start when the setting is true and refreshes the
/// quoted path after an upgrade or a moved folder — it deliberately never opens <see cref="ApprovedKey"/>, so a
/// Task Manager "Disabled" survives a manual launch (RESEARCH Pitfall 9, T-03-13). Only the checkbox handler
/// (<see cref="Enable"/> / <see cref="Disable"/>) writes the approval bytes.
/// </para>
/// Every method catches the registry exception triple (<see cref="IOException"/>,
/// <see cref="UnauthorizedAccessException"/>, <see cref="System.Security.SecurityException"/>), logs
/// <c>autostart registry failed op=&lt;op&gt;</c> and returns <see langword="false"/> — never a throw into the
/// window, never a dialog (T-03-15).
/// </summary>
[SupportedOSPlatform("windows8.0")]
public static class RegistryAutostartManager
{
    /// <summary>The value name under both keys; Phase 4's installer <c>[Registry]</c> entry uses the same name.</summary>
    public const string ValueName = "BingWallpaperUpdater";

    /// <summary>The per-user Run key (REG_SZ command lines executed by Explorer at logon).</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Task Manager's approval key (REG_BINARY, 12 bytes per entry; see <see cref="StartupApprovedState"/>).</summary>
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The observed state: the Run value is present as a string AND Task Manager has not disabled it.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
            {
                return false;
            }

            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return StartupApprovedState.IsEnabled(approved?.GetValue(ValueName) as byte[]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"autostart registry failed op=read error={ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes (or overwrites — SetValue is idempotent) the quoted <c>--startup</c> command line for
    /// <paramref name="exePath"/> under <see cref="RunKey"/>. Never touches <see cref="ApprovedKey"/>.
    /// </summary>
    public static bool RewriteRunValue(string exePath)
    {
        try
        {
            using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            run.SetValue(ValueName, AutostartCommand.For(exePath), RegistryValueKind.String);
            Log.Info($"autostart run value written path={exePath}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"autostart registry failed op=rewrite error={ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The checkbox turned on: writes the Run value and an "enabled" approval value (re-enabling an entry the user
    /// had disabled in Task Manager — otherwise the Run value would never execute).
    /// </summary>
    public static bool Enable(string exePath)
    {
        if (!RewriteRunValue(exePath))
        {
            return false;
        }

        try
        {
            using RegistryKey approved = Registry.CurrentUser.CreateSubKey(ApprovedKey, writable: true);
            approved.SetValue(ValueName, StartupApprovedState.Encode(true, DateTimeOffset.UtcNow), RegistryValueKind.Binary);
            Log.Info("autostart enabled");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"autostart registry failed op=enable error={ex.Message}");
            return false;
        }
    }

    /// <summary>The checkbox turned off: deletes the value under both keys (a missing value is a no-op, so Disable twice is safe).</summary>
    public static bool Disable()
    {
        try
        {
            using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            using (RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
            {
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Info("autostart disabled");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"autostart registry failed op=disable error={ex.Message}");
            return false;
        }
    }
}
