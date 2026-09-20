using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Windows.Power;

namespace BingWallpaperUpdater.App;

/// <summary>
/// Hidden top-level <see cref="NativeWindow"/> that receives <c>WM_POWERBROADCAST</c> on the UI thread and turns
/// <c>PBT_APMRESUMEAUTOMATIC</c> and a console-display-state transition to "on" into the <see cref="Resumed"/> event
/// (D-10, RESEARCH Pattern 5). The window is created invisible with default styles and never shown (D-15); it is
/// top-level rather than a message-only child window because message-only windows do not receive broadcast
/// messages, and it is additionally registered with <see cref="PowerNotifications.Register"/> so registered
/// notifications arrive even where the plain broadcast does not.
/// The handler must only re-arm the heartbeat (<c>RotationService.Nudge</c>); it never runs a tick.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class PowerWindow : NativeWindow, IDisposable
{
    /// <summary>Window caption; <c>tools/power-probe.ps1</c> locates the window with <c>FindWindow(null, Caption)</c>.</summary>
    public const string Caption = "BingWallpaperUpdater.PowerWindow";

    private readonly PowerRegistration _registration;
    private int? _lastDisplayState;
    private bool _disposed;

    /// <summary>Raised on the UI thread with the source token: <c>resume-automatic</c> or <c>display-on</c>.</summary>
    public event Action<string>? Resumed;

    /// <summary>Creates the hidden window on the calling (UI) thread and registers for power notifications.</summary>
    public PowerWindow()
    {
        CreateHandle(new CreateParams { Caption = Caption });
        _registration = PowerNotifications.Register(Handle);
        Log.Info($"power window hwnd=0x{Handle:X} suspendResume={(_registration.HasSuspendResume ? "ok" : "-")} displayState={(_registration.HasDisplayState ? "ok" : "-")}");
    }

    protected override void WndProc(ref Message m)
    {
        try
        {
            if (m.Msg == PowerNotifications.WmPowerBroadcast)
            {
                switch ((int)m.WParam)
                {
                    case PowerNotifications.PbtApmResumeAutomatic:
                        Resumed?.Invoke("resume-automatic");
                        m.Result = 1;
                        return;

                    case PowerNotifications.PbtPowerSettingChange:
                        if (PowerNotifications.TryReadConsoleDisplayState(m.LParam, out int state))
                        {
                            bool changed = _lastDisplayState.HasValue && _lastDisplayState.Value != state;
                            _lastDisplayState = state;
                            if (changed && state == 1)
                            {
                                Resumed?.Invoke("display-on");
                            }
                        }

                        m.Result = 1;
                        return;

                    // PBT_APMRESUMESUSPEND follows PBT_APMRESUMEAUTOMATIC on user-initiated wakes and would double-signal;
                    // PBT_APMSUSPEND needs no action (the heartbeat simply stops firing while asleep).
                }
            }
        }
        catch (Exception ex)
        {
            // A message handler must never throw into the message loop.
            Log.Warn("power window error", ex);
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>Unregisters both notifications and destroys the window. Idempotent; call on the creating thread.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration.Dispose();
        DestroyHandle();
    }
}
