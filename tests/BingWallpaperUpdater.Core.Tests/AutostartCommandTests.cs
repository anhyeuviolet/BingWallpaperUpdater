using BingWallpaperUpdater.Core.Autostart;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// INST-02 / T-03-12: the Run command line is always the quoted absolute exe path plus <c>--startup</c>; relative
/// paths, embedded quotes and blanks are rejected rather than repaired. Pure: no registry. The rooted-path rows use
/// Windows paths because the app only ever runs (and this suite is only ever run) on Windows.
/// </summary>
public sealed class AutostartCommandTests
{
    [Fact]
    public void For_RootedPathWithSpaces_QuotesAndAppendsFlag()
    {
        const string exe = @"C:\Users\Nguyen Van A\AppData\Local\Programs\BingWallpaperUpdater\BingWallpaperUpdater.exe";
        Assert.Equal(
            "\"C:\\Users\\Nguyen Van A\\AppData\\Local\\Programs\\BingWallpaperUpdater\\BingWallpaperUpdater.exe\" --startup",
            AutostartCommand.For(exe));
    }

    [Fact]
    public void For_RootedPathWithoutSpaces_IsStillQuoted()
    {
        Assert.Equal("\"D:\\Apps\\BingWallpaperUpdater.exe\" --startup", AutostartCommand.For(@"D:\Apps\BingWallpaperUpdater.exe"));
    }

    [Fact]
    public void For_IsIdempotentForTheSamePath()
    {
        const string exe = @"C:\Program Files\X\a.exe";
        Assert.Equal(AutostartCommand.For(exe), AutostartCommand.For(exe));
    }

    [Theory]
    [InlineData(@"publish\BingWallpaperUpdater.exe")]
    [InlineData("BingWallpaperUpdater.exe")]
    [InlineData(@"..\BingWallpaperUpdater.exe")]
    public void For_RelativePath_Throws(string relative)
    {
        Assert.Throws<ArgumentException>(() => AutostartCommand.For(relative));
    }

    [Fact]
    public void For_PathWithQuote_Throws()
    {
        Assert.Throws<ArgumentException>(() => AutostartCommand.For("C:\\Apps\\a\" --evil \"b.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void For_Blank_Throws(string blank)
    {
        Assert.Throws<ArgumentException>(() => AutostartCommand.For(blank));
    }

    [Fact]
    public void For_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AutostartCommand.For(null!));
    }

    [Fact]
    public void Flag_IsDashDashStartup()
    {
        Assert.Equal("--startup", AutostartCommand.Flag);
    }
}
