using System.Runtime.InteropServices;
using BingWallpaperUpdater.Core.Ports;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// WR-04: the generated COM wrappers throw through <c>Marshal.ThrowExceptionForHR</c>, which maps HRESULTs to
/// several exception types. <see cref="Marshal.GetExceptionForHR(int)"/> produces exactly those instances, so
/// the classification is pinned against the real mapping without touching COM.
/// </summary>
public sealed class ComFailureTests
{
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
    private const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

    private static Exception For(int hr) => Marshal.GetExceptionForHR(hr) ?? throw new InvalidOperationException("no exception for HRESULT");

    [Fact]
    public void ThrowExceptionForHR_MapsToSeveralTypes_NotOnlyCOMException()
    {
        Assert.IsType<UnauthorizedAccessException>(For(E_ACCESSDENIED));
        Assert.IsType<ArgumentException>(For(E_INVALIDARG));
        Assert.IsType<NotImplementedException>(For(E_NOTIMPL));
        Assert.IsType<InvalidCastException>(For(E_NOINTERFACE));
        Assert.IsType<COMException>(For(E_FAIL));
        Assert.IsType<COMException>(For(REGDB_E_CLASSNOTREG));
        Assert.IsType<COMException>(For(CO_E_NOTINITIALIZED));
        Assert.IsType<OutOfMemoryException>(For(E_OUTOFMEMORY));
    }

    [Theory]
    [InlineData(REGDB_E_CLASSNOTREG)]
    [InlineData(CO_E_NOTINITIALIZED)]
    [InlineData(E_NOINTERFACE)]
    [InlineData(E_NOTIMPL)]
    [InlineData(E_ACCESSDENIED)]
    [InlineData(E_FAIL)]
    public void IsActivationFailure_AdmitsEveryActivationHResult(int hr)
    {
        Assert.True(ComFailure.IsActivationFailure(For(hr)));
    }

    [Theory]
    [InlineData(E_INVALIDARG)]
    [InlineData(E_ACCESSDENIED)]
    [InlineData(E_FAIL)]
    [InlineData(E_NOINTERFACE)]
    [InlineData(E_NOTIMPL)]
    public void IsCallFailure_AdmitsEveryShellCallHResult(int hr)
    {
        Assert.True(ComFailure.IsCallFailure(For(hr)));
    }

    [Fact]
    public void NeitherFilter_SwallowsOutOfMemoryOrUnrelatedExceptions()
    {
        Assert.False(ComFailure.IsActivationFailure(For(E_OUTOFMEMORY)));
        Assert.False(ComFailure.IsCallFailure(For(E_OUTOFMEMORY)));
        Assert.False(ComFailure.IsActivationFailure(new InvalidOperationException()));
        Assert.False(ComFailure.IsCallFailure(new InvalidOperationException()));
        Assert.False(ComFailure.IsCallFailure(new IOException()));
    }
}
