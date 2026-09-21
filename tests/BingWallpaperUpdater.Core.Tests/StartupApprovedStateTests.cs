using BingWallpaperUpdater.Core.Autostart;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// INST-02: the 12-byte <c>StartupApproved</c> codec against the byte patterns observed on a real machine
/// (RESEARCH Pattern 7): <c>02</c> / <c>06</c> enabled, <c>03</c> / <c>07</c> + FILETIME disabled, the undocumented
/// <c>01</c> + zero time and short / missing values are enabled (Assumption A1). Pure: no registry, no clock.
/// </summary>
public sealed class StartupApprovedStateTests
{
    // Lync, disabled in Task Manager: bytes 4..11 = FILETIME of the disable, little-endian.
    private static readonly byte[] Disabled03 = [0x03, 0x00, 0x00, 0x00, 0x24, 0xC8, 0xFB, 0x3D, 0x2E, 0x8F, 0xDB, 0x01];
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 45, 0, TimeSpan.Zero);

    [Fact]
    public void IsEnabled_Enabled02_True()
    {
        Assert.True(StartupApprovedState.IsEnabled([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void IsEnabled_Enabled06_True()
    {
        Assert.True(StartupApprovedState.IsEnabled([0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void IsEnabled_Disabled03WithFileTime_False()
    {
        Assert.False(StartupApprovedState.IsEnabled(Disabled03));
    }

    [Fact]
    public void IsEnabled_Disabled07WithFileTime_False()
    {
        byte[] data = (byte[])Disabled03.Clone();
        data[0] = 0x07;
        Assert.False(StartupApprovedState.IsEnabled(data));
    }

    [Fact]
    public void IsEnabled_Undocumented01ZeroTime_True()
    {
        // Docker Desktop on the probed machine: low bit set but no timestamp -> treated as enabled (A1).
        Assert.True(StartupApprovedState.IsEnabled([0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void IsEnabled_Null_True()
    {
        Assert.True(StartupApprovedState.IsEnabled(null));
    }

    [Fact]
    public void IsEnabled_ThreeBytes_True()
    {
        Assert.True(StartupApprovedState.IsEnabled([0x03, 0x00, 0x00]));
    }

    [Fact]
    public void IsEnabled_Flag03ButZeroTime_True()
    {
        Assert.True(StartupApprovedState.IsEnabled([0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void IsEnabled_Flag03ButOnlyFourBytes_True()
    {
        // No timestamp can follow a 4-byte value, so it cannot say "disabled".
        Assert.True(StartupApprovedState.IsEnabled([0x03, 0, 0, 0]));
    }

    [Fact]
    public void Encode_Enabled_Is12BytesStarting02AllElseZero()
    {
        byte[] data = StartupApprovedState.Encode(true, Now);
        Assert.Equal(12, data.Length);
        Assert.Equal(0x02, data[0]);
        Assert.All(data.Skip(1), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Encode_Disabled_Starts03AndCarriesFileTimeOfNow()
    {
        byte[] data = StartupApprovedState.Encode(false, Now);
        Assert.Equal(12, data.Length);
        Assert.Equal(0x03, data[0]);
        Assert.Equal([0, 0, 0], data[1..4]);
        long fileTime = BitConverter.ToInt64(data, 4);   // little-endian on every supported target
        Assert.Equal(Now.UtcDateTime, DateTime.FromFileTimeUtc(fileTime));
    }

    [Fact]
    public void Encode_Disabled_IgnoresTheOffsetOfNow()
    {
        DateTimeOffset local = Now.ToOffset(TimeSpan.FromHours(7));
        Assert.Equal(StartupApprovedState.Encode(false, Now), StartupApprovedState.Encode(false, local));
    }

    [Fact]
    public void Encode_RoundTrips_ThroughIsEnabled()
    {
        Assert.True(StartupApprovedState.IsEnabled(StartupApprovedState.Encode(true, Now)));
        Assert.False(StartupApprovedState.IsEnabled(StartupApprovedState.Encode(false, Now)));
    }

    [Fact]
    public void Encode_ReturnsAFreshArrayEachCall()
    {
        byte[] a = StartupApprovedState.Encode(true, Now);
        byte[] b = StartupApprovedState.Encode(true, Now);
        Assert.NotSame(a, b);
    }
}
