using System.Globalization;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Display;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// SRC-07 "Auto" resolution rule: explicit settings pass through, no monitors means UHD, otherwise the largest attached
/// monitor by area picks the smallest concrete resolution that covers it. Pure: no LogSink, no temp dir.
/// </summary>
public sealed class ResolutionPolicyTests
{
    /// <summary>"1920x1080,3840x2160" -> the monitor list; "" -> no monitors.</summary>
    private static IReadOnlyList<(int Width, int Height)> Monitors(string spec) =>
        spec.Length == 0
            ? []
            : spec.Split(',').Select(m =>
            {
                string[] parts = m.Split('x');
                return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
            }).ToList();

    [Theory]
    [InlineData("UHD", "1366x768", "UHD")]                        // explicit settings ignore the layout
    [InlineData("1920x1080", "3840x2160", "1920x1080")]
    [InlineData("1920x1200", "", "1920x1200")]
    [InlineData("Auto", "", "UHD")]                               // nothing enumerated -> full quality
    [InlineData("Auto", "1366x768", "1920x1080")]
    [InlineData("Auto", "1536x864", "1920x1080")]
    [InlineData("Auto", "1920x1080", "1920x1080")]
    [InlineData("Auto", "1920x1200", "1920x1200")]
    [InlineData("Auto", "2560x1440", "UHD")]
    [InlineData("Auto", "3440x1440", "UHD")]
    [InlineData("Auto", "3840x2160", "UHD")]
    [InlineData("Auto", "1920x1080,3840x2160", "UHD")]            // largest wins
    [InlineData("Auto", "3840x2160,1920x1080", "UHD")]            // order does not matter
    [InlineData("Auto", "1920x1200,1366x768", "1920x1200")]
    [InlineData("Auto", "1080x1920", "UHD")]                      // a portrait 1080p panel is taller than 1200
    public void Resolve_MapsTheLargestMonitorToOneConcreteResolution(string setting, string monitors, string expected)
    {
        Assert.Equal(expected, ResolutionPolicy.Resolve(setting, Monitors(monitors)));
    }

    [Theory]
    [InlineData("Auto", "1366x768")]
    [InlineData("Auto", "1920x1200")]
    [InlineData("Auto", "3840x2160")]
    [InlineData("Auto", "")]
    [InlineData("UHD", "1366x768")]
    [InlineData("1920x1080", "3840x2160")]
    [InlineData("1920x1200", "1366x768")]
    public void Resolve_AlwaysYieldsAValueMinDimensionsAccepts(string setting, string monitors)
    {
        string resolved = ResolutionPolicy.Resolve(setting, Monitors(monitors));

        Assert.Contains(resolved, Settings.KnownResolutions.Where(r => r != Settings.AutoResolution));
        Assert.Null(Record.Exception(() => BingImageUrl.MinDimensions(resolved)));
    }

    [Fact]
    public void Resolve_IsCaseSensitiveOnAuto_SoOnlyTheCanonicalValueIsResolved()
    {
        // Settings.Sanitize canonicalises "auto" to "Auto" before the tick; the policy itself is strict (ordinal).
        Assert.Equal("auto", ResolutionPolicy.Resolve("auto", Monitors("1366x768")));
        Assert.Equal("1920x1080", ResolutionPolicy.Resolve(Settings.AutoResolution, Monitors("1366x768")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_EmptySetting_Throws(string? setting)
    {
        Assert.ThrowsAny<ArgumentException>(() => ResolutionPolicy.Resolve(setting!, Monitors("1920x1080")));
    }

    [Fact]
    public void Resolve_NullMonitors_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ResolutionPolicy.Resolve("Auto", null!));
    }
}
