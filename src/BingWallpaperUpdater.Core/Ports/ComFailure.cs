using System.Runtime.InteropServices;

namespace BingWallpaperUpdater.Core.Ports;

/// <summary>
/// Classifies the exceptions <c>Marshal.ThrowExceptionForHR</c> raises out of source-generated COM wrappers, so
/// the Windows adapter can tell "activation failed — degrade to the SPI fallback" from "the shell rejected the call —
/// report <see cref="ApplyResult"/>(false)". The mapping is not one type: <c>E_ACCESSDENIED</c> becomes
/// <see cref="UnauthorizedAccessException"/>, <c>E_INVALIDARG</c> <see cref="ArgumentException"/>, <c>E_NOTIMPL</c>
/// <see cref="NotImplementedException"/>, <c>E_NOINTERFACE</c> <see cref="InvalidCastException"/>, and only the
/// remaining HRESULTs (<c>E_FAIL</c>, <c>REGDB_E_CLASSNOTREG</c>, <c>CO_E_NOTINITIALIZED</c>, ...) a
/// <see cref="COMException"/>. <see cref="OutOfMemoryException"/> (<c>E_OUTOFMEMORY</c>) is deliberately in neither
/// set. Pure, so it is unit-tested without COM (WR-04).
/// </summary>
public static class ComFailure
{
    /// <summary>
    /// <c>CoCreateInstance</c> / <c>QueryInterface</c> could not produce the interface on this session: class not
    /// registered, COM not initialised, interface unavailable or not implemented, access denied.
    /// </summary>
    public static bool IsActivationFailure(Exception ex) =>
        ex is COMException
            or InvalidCastException
            or NotSupportedException
            or NotImplementedException
            or UnauthorizedAccessException;

    /// <summary>A call on a live proxy failed with an HRESULT the shell reported (bad path, denied, unsupported).</summary>
    public static bool IsCallFailure(Exception ex) =>
        ex is ExternalException
            or ArgumentException
            or UnauthorizedAccessException
            or InvalidCastException
            or NotSupportedException
            or NotImplementedException;
}
