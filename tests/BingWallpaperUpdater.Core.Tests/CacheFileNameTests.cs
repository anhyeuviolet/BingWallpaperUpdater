using System.Text.RegularExpressions;
using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>CACHE-03 naming: date fallback chain, resolution suffix, and the encoding boundary (T-01-02).</summary>
public sealed class CacheFileNameTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 3, 0, 0, TimeSpan.Zero);
    private static readonly Regex Shape = new(@"^\d{4}-\d{2}-\d{2}_[A-Za-z0-9]+(\.\d+x\d+)?\.jpg$", RegexOptions.CultureInvariant);

    private static CatalogEntry Entry(string id = "OHR.AlphornBavaria_EN-US6200857270", string? date = "2026-09-20", string? startDate = "20260919")
    {
        Assert.True(ImageId.TryParse(id, out ImageId parsed));
        return new CatalogEntry(parsed, date, startDate, null, null, null, CatalogSources.GitHub);
    }

    [Fact]
    public void For_Uhd_UsesCatalogDateAndPlainJpgSuffix()
    {
        Assert.Equal("2026-09-20_AlphornBavaria.jpg", CacheFileName.For(Entry(), "UHD", Now));
    }

    [Fact]
    public void For_NonUhd_AppendsResolutionBeforeTheExtension()
    {
        Assert.Equal("2026-09-20_AlphornBavaria.1920x1080.jpg", CacheFileName.For(Entry(), "1920x1080", Now));
        Assert.Equal("2026-09-20_AlphornBavaria.1920x1200.jpg", CacheFileName.For(Entry(), "1920x1200", Now));
    }

    [Fact]
    public void For_UhdIsCaseInsensitive()
    {
        Assert.Equal("2026-09-20_AlphornBavaria.jpg", CacheFileName.For(Entry(), "uhd", Now));
    }

    [Fact]
    public void For_StartDateFallback_ReformatsBingYyyyMmDd()
    {
        Assert.Equal("2026-09-19_AlphornBavaria.jpg", CacheFileName.For(Entry(date: null), "UHD", Now));
    }

    [Fact]
    public void For_NowFallback_UsesTheUtcDownloadDateWhenBothDatesAreNull()
    {
        Assert.Equal("2026-09-21_AlphornBavaria.jpg", CacheFileName.For(Entry(date: null, startDate: null), "UHD", Now));
    }

    [Theory]
    [InlineData("2026-09-20", "20260919", "2026-09-20")]
    [InlineData("", "20260919", "2026-09-19")]
    [InlineData("not-a-date", "20260919", "2026-09-19")]
    [InlineData("2026-9-20", "20260919", "2026-09-19")]
    [InlineData(null, "2026091", "2026-09-21")]
    [InlineData(null, "", "2026-09-21")]
    [InlineData(null, null, "2026-09-21")]
    public void DatePrefix_FallsThroughTheChainOnlyForMalformedInput(string? catalogDate, string? startDate, string expected)
    {
        Assert.Equal(expected, CacheFileName.DatePrefix(catalogDate, startDate, Now));
    }

    [Theory]
    [InlineData("OHR.AlphornBavaria_EN-US6200857270", "UHD")]
    [InlineData("OHR.AlphornBavaria_EN-US6200857270", "1920x1080")]
    [InlineData("OHR.A_EN-US1", "UHD")]
    [InlineData("OHR.Abc123XYZ_DE-DE0", "1920x1200")]
    [InlineData("OHR.x9_ZH-CN99999999999999999999", "UHD")]
    public void For_AlwaysMatchesTheSafeShape_ForEveryAcceptedImageId(string id, string resolution)
    {
        string name = CacheFileName.For(Entry(id), resolution, Now);

        Assert.Matches(Shape, name);
        Assert.Equal(name, Path.GetFileName(name));
    }

    [Theory]
    [InlineData("OHR.Alp/horn_EN-US1")]
    [InlineData("OHR.Alp..horn_EN-US1")]
    [InlineData("OHR.Ålphorn_EN-US1")]
    [InlineData("OHR.Alp\0horn_EN-US1")]
    [InlineData("OHR.Alp\\horn_EN-US1")]
    [InlineData("OHR.Alp horn_EN-US1")]
    [InlineData("OHR._EN-US1")]
    [InlineData("")]
    [InlineData(null)]
    public void ImageIdTryParse_RejectsAnythingThatCouldReachAPath(string? text)
    {
        Assert.False(ImageId.TryParse(text, out _));
    }

    [Fact]
    public void For_UnknownResolutionShape_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => CacheFileName.For(Entry(), "Auto", Now));
        Assert.ThrowsAny<ArgumentException>(() => CacheFileName.For(Entry(), "../x", Now));
    }
}
