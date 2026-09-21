using System.Text.RegularExpressions;
using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>CACHE-03 naming: date fallback chain, resolution suffix, and the encoding boundary (T-01-02).</summary>
public sealed class CacheFileNameTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 3, 0, 0, TimeSpan.Zero);
    private static readonly Regex Shape = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}_[A-Za-z0-9]+_([A-Z]{2}-[A-Z]{2}|ROW)[0-9]+(\.[0-9]+x[0-9]+)?\.jpg$", RegexOptions.CultureInvariant);

    private static CatalogEntry Entry(string id = "OHR.AlphornBavaria_EN-US6200857270", string? date = "2026-09-20", string? startDate = "20260919")
    {
        Assert.True(ImageId.TryParse(id, out ImageId parsed));
        return new CatalogEntry(parsed, date, startDate, null, null, null, CatalogSources.GitHub);
    }

    [Fact]
    public void For_Uhd_UsesCatalogDateAndPlainJpgSuffix()
    {
        Assert.Equal("2026-09-20_AlphornBavaria_EN-US6200857270.jpg", CacheFileName.For(Entry(), "UHD", Now));
    }

    [Fact]
    public void For_NonUhd_AppendsResolutionBeforeTheExtension()
    {
        Assert.Equal("2026-09-20_AlphornBavaria_EN-US6200857270.1920x1080.jpg", CacheFileName.For(Entry(), "1920x1080", Now));
        Assert.Equal("2026-09-20_AlphornBavaria_EN-US6200857270.1920x1200.jpg", CacheFileName.For(Entry(), "1920x1200", Now));
    }

    [Fact]
    public void For_UhdIsCaseInsensitive()
    {
        Assert.Equal("2026-09-20_AlphornBavaria_EN-US6200857270.jpg", CacheFileName.For(Entry(), "uhd", Now));
    }

    [Fact]
    public void For_StartDateFallback_ReformatsBingYyyyMmDd()
    {
        Assert.Equal("2026-09-19_AlphornBavaria_EN-US6200857270.jpg", CacheFileName.For(Entry(date: null), "UHD", Now));
    }

    [Fact]
    public void For_NowFallback_UsesTheUtcDownloadDateWhenBothDatesAreNull()
    {
        Assert.Equal("2026-09-21_AlphornBavaria_EN-US6200857270.jpg", CacheFileName.For(Entry(date: null, startDate: null), "UHD", Now));
    }

    /// <summary>WR-01: the name is a function of the whole ID, so IDs that share only the Name segment never share a file.</summary>
    [Theory]
    [InlineData("OHR.AlphornBavaria_EN-US6200857270", "OHR.AlphornBavaria_DE-DE1234567890")]
    [InlineData("OHR.AlphornBavaria_EN-US6200857270", "OHR.AlphornBavaria_EN-US6200857271")]
    [InlineData("OHR.AlphornBavaria_EN-US6200857270", "OHR.AlphornBavaria_EN-US62008572700")]
    public void For_DistinctIdsWithTheSameNameAndDate_YieldDistinctFileNames(string first, string second)
    {
        string a = CacheFileName.For(Entry(first), "UHD", Now);
        string b = CacheFileName.For(Entry(second), "UHD", Now);

        Assert.NotEqual(a, b, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(first["OHR.".Length..], a, StringComparison.Ordinal);
        Assert.Contains(second["OHR.".Length..], b, StringComparison.Ordinal);
    }

    /// <summary>The digit class is ASCII-only: a Unicode digit in the suffix must be rejected, not reach a file name.</summary>
    [Fact]
    public void ImageIdTryParse_RejectsNonAsciiDigitsInTheSuffix()
    {
        Assert.False(ImageId.TryParse("OHR.AlphornBavaria_EN-US٣٤", out _));
        Assert.True(ImageId.TryParse("OHR.AlphornBavaria_EN-US34", out ImageId id));
        Assert.Equal("34", id.Digits);
    }

    /// <summary>SRC-04: Bing answers en-AU / vi-VN with the "rest of world" pseudo-market ROW; the ID must parse and keep ROW as its market.</summary>
    [Fact]
    public void TryParse_RowMarket_ParsesAndKeepsRowAsMarket()
    {
        Assert.True(ImageId.TryParse("OHR.ParisSunset_ROW1775373883", out ImageId id));

        Assert.Equal("ROW", id.Market);
        Assert.Equal("ParisSunset", id.Name);
        Assert.Equal("1775373883", id.Digits);
    }

    /// <summary>The widened market group is a bounded literal alternation (T-03-08): near-misses of ROW are still rejected.</summary>
    [Theory]
    [InlineData("OHR.X_RO1")]
    [InlineData("OHR.X_ROWW1")]
    [InlineData("OHR.X_row1")]
    [InlineData("OHR.X_ROW")]
    [InlineData("OHR.X_RO-W1")]
    public void TryParse_RowNearMisses_AreRejected(string text)
    {
        Assert.False(ImageId.TryParse(text, out _));
    }

    /// <summary>A ROW ID lands in the cache under the same stem rule; every character is in the safe set, so CacheFileName needs no change.</summary>
    [Fact]
    public void For_RowMarket_ProducesValidFileName()
    {
        CatalogEntry entry = Entry("OHR.ParisSunset_ROW1775373883", date: "2026-09-21", startDate: "20260920");

        Assert.Equal("2026-09-21_ParisSunset_ROW1775373883.jpg", CacheFileName.For(entry, "UHD", Now));
        Assert.Equal("2026-09-21_ParisSunset_ROW1775373883.1920x1080.jpg", CacheFileName.For(entry, "1920x1080", Now));
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
    [InlineData("OHR.ParisSunset_ROW1775373883", "UHD")]
    [InlineData("OHR.ParisSunset_ROW1775373883", "1920x1200")]
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
