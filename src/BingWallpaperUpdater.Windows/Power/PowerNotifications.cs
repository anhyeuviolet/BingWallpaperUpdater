using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace BingWallpaperUpdater.Windows.Power;

/// <summary>
/// Public wrappers over the CsWin32-generated (internal) power notification P/Invokes, so the hidden
/// <c>PowerWindow</c> in the App project can register for suspend/resume and console-display-state
/// notifications and decode the resulting <c>WM_POWERBROADCAST</c> messages (D-10, RESEARCH Pattern 5).
/// Registration failures are logged and tolerated: the app keeps running on the 60 s heartbeat alone.
/// </summary>
[SupportedOSPlatform("windows8.0")]
public static unsafe class PowerNotifications
{
    /// <summary>WM_POWERBROADCAST (536).</summary>
    public const int WmPowerBroadcast = 0x218;

    /// <summary>PBT_APMSUSPEND (4): the system is about to suspend.</summary>
    public const int PbtApmSuspend = 4;

    /// <summary>PBT_APMRESUMESUSPEND (7): resumed after user input; always follows PBT_APMRESUMEAUTOMATIC.</summary>
    public const int PbtApmResumeSuspend = 7;

    /// <summary>PBT_APMRESUMEAUTOMATIC (18): the system resumed (always sent on every resume).</summary>
    public const int PbtApmResumeAutomatic = 0x12;

    /// <summary>PBT_POWERSETTINGCHANGE (32787): lParam points at a POWERBROADCAST_SETTING.</summary>
    public const int PbtPowerSettingChange = 0x8013;

    private const string SuspendResumeKind = "suspend-resume";
    private const string DisplayStateKind = "display-state";

    static PowerNotifications()
    {
        // A CsWin32 update cannot silently diverge from the public mirrors above.
        Debug.Assert((uint)WmPowerBroadcast == PInvoke.WM_POWERBROADCAST, "WM_POWERBROADCAST mismatch");
        Debug.Assert((uint)PbtApmSuspend == PInvoke.PBT_APMSUSPEND, "PBT_APMSUSPEND mismatch");
        Debug.Assert((uint)PbtApmResumeSuspend == PInvoke.PBT_APMRESUMESUSPEND, "PBT_APMRESUMESUSPEND mismatch");
        Debug.Assert((uint)PbtApmResumeAutomatic == PInvoke.PBT_APMRESUMEAUTOMATIC, "PBT_APMRESUMEAUTOMATIC mismatch");
        Debug.Assert((uint)PbtPowerSettingChange == PInvoke.PBT_POWERSETTINGCHANGE, "PBT_POWERSETTINGCHANGE mismatch");
    }

    /// <summary>
    /// Registers <paramref name="hwnd"/> for suspend/resume notifications and for GUID_CONSOLE_DISPLAY_STATE
    /// changes (DEVICE_NOTIFY_WINDOW_HANDLE). A failed registration logs
    /// <c>power registration failed kind=&lt;suspend-resume|display-state&gt; error=&lt;n&gt;</c> and leaves that
    /// handle null; never throws. Dispose the returned registration on the creating thread before the window dies.
    /// </summary>
    public static PowerRegistration Register(nint hwnd)
    {
        var recipient = new HANDLE(hwnd);

        HPOWERNOTIFY suspend = PInvoke.RegisterSuspendResumeNotification(recipient, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (suspend.IsNull)
        {
            Log.Warn($"power registration failed kind={SuspendResumeKind} error={Marshal.GetLastPInvokeError()}");
        }

        Guid setting = PInvoke.GUID_CONSOLE_DISPLAY_STATE;
        HPOWERNOTIFY display = PInvoke.RegisterPowerSettingNotification(recipient, &setting, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (display.IsNull)
        {
            Log.Warn($"power registration failed kind={DisplayStateKind} error={Marshal.GetLastPInvokeError()}");
        }

        return new PowerRegistration(suspend, display);
    }

    /// <summary>
    /// Decodes the lParam of a PBT_POWERSETTINGCHANGE message. True only when it points at a
    /// POWERBROADCAST_SETTING for GUID_CONSOLE_DISPLAY_STATE carrying at least four data bytes;
    /// <paramref name="state"/> is then 0 (off), 1 (on) or 2 (dimmed).
    /// </summary>
    public static bool TryReadConsoleDisplayState(nint lParam, out int state)
    {
        state = 0;
        if (lParam == 0)
        {
            return false;
        }

        var setting = (POWERBROADCAST_SETTING*)lParam;
        if (setting->PowerSetting != PInvoke.GUID_CONSOLE_DISPLAY_STATE || setting->DataLength < sizeof(int))
        {
            return false;
        }

        // Data is a VariableLengthInlineArray<byte>; its first four bytes are the DWORD display state.
        byte* data = (byte*)&setting->Data;
        state = *(int*)data;
        return true;
    }

    internal static void Unregister(ref HPOWERNOTIFY suspend, ref HPOWERNOTIFY display)
    {
        if (!suspend.IsNull)
        {
            if (!PInvoke.UnregisterSuspendResumeNotification(suspend))
            {
                Log.Warn($"power unregistration failed kind={SuspendResumeKind} error={Marshal.GetLastPInvokeError()}");
            }

            suspend = default;
        }

        if (!display.IsNull)
        {
            if (!PInvoke.UnregisterPowerSettingNotification(display))
            {
                Log.Warn($"power unregistration failed kind={DisplayStateKind} error={Marshal.GetLastPInvokeError()}");
            }

            display = default;
        }
    }
}

/// <summary>
/// The pair of HPOWERNOTIFY handles returned by <see cref="PowerNotifications.Register"/>. Either may be null when
/// that registration failed. <see cref="Dispose"/> unregisters the non-null handles exactly once.
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed class PowerRegistration : IDisposable
{
    private HPOWERNOTIFY _suspendResume;
    private HPOWERNOTIFY _displayState;

    internal PowerRegistration(HPOWERNOTIFY suspendResume, HPOWERNOTIFY displayState)
    {
        _suspendResume = suspendResume;
        _displayState = displayState;
    }

    /// <summary>True when the suspend/resume registration succeeded.</summary>
    public bool HasSuspendResume => !_suspendResume.IsNull;

    /// <summary>True when the GUID_CONSOLE_DISPLAY_STATE registration succeeded.</summary>
    public bool HasDisplayState => !_displayState.IsNull;

    public void Dispose() => PowerNotifications.Unregister(ref _suspendResume, ref _displayState);
}
