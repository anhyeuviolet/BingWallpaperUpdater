using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>SRC-06 / SRC-03: every URL is a pure function of (host, ID, resolution); nothing is copied from remote text.</summary>
public sealed class BingImageUrlTests
{
    private static readonly ImageId Id = new("OHR.AlphornBavaria_EN-US6200857270");

    [Fact]
    public void Image_Uhd_IsExactRawUrlWithoutQuery()
    {
        Uri url = BingImageUrl.Image("www.bing.com", Id, null, null);

        Assert.Equal("https://www.bing.com/th?id=OHR.AlphornBavaria_EN-US6200857270_UHD.jpg", url.AbsoluteUri);
        Assert.Equal("?id=OHR.AlphornBavaria_EN-US6200857270_UHD.jpg", url.Query);
        Assert.DoesNotContain("&", url.AbsoluteUri);
        Assert.DoesNotContain("w=", url.AbsoluteUri);
    }

    [Fact]
    public void Image_IsPureAndIdenticalOnEveryCall()
    {
        Uri first = BingImageUrl.Image("www.bing.com", Id, null, null);
        Uri second = BingImageUrl.Image("www.bing.com", Id, null, null);

        Assert.Equal(first, second);
        Assert.Equal(first.AbsoluteUri, second.AbsoluteUri);
    }

    [Fact]
    public void Image_Resized_AppendsCdnResizeParameters()
    {
        Uri url = BingImageUrl.Image("cn.bing.com", Id, 1920, 1080);

        Assert.EndsWith("_UHD.jpg&w=1920&h=1080&rs=1&c=4", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("cn.bing.com", url.Host);
    }

    [Fact]
    public void Image_MixedNullDimensions_Throws()
    {
        Assert.Throws<ArgumentException>(() => BingImageUrl.Image("www.bing.com", Id, 1920, null));
        Assert.Throws<ArgumentException>(() => BingImageUrl.Image("www.bing.com", Id, null, 1080));
    }

    [Fact]
    public void Image_SameIdOnBothHosts_HasIdenticalPathAndQuery()
    {
        Uri www = BingImageUrl.Image(BingImageUrl.PrimaryHost, Id, null, null);
        Uri cn = BingImageUrl.Image(BingImageUrl.RetryHost, Id, null, null);

        Assert.Equal(www.PathAndQuery, cn.PathAndQuery);
        Assert.Equal("www.bing.com", www.Host);
        Assert.Equal("cn.bing.com", cn.Host);
    }

    [Fact]
    public void Archive_Default_IsExactEndpoint()
    {
        Uri url = BingImageUrl.Archive("www.bing.com", 0, 8, "en-US");

        Assert.Equal("https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=en-US", url.AbsoluteUri);
    }

    [Theory]
    [InlineData(8, 8)]
    [InlineData(0, 0)]
    [InlineData(0, 9)]
    [InlineData(-1, 8)]
    [InlineData(7, 16)]
    public void Archive_OutOfRangeIdxOrN_Throws(int idx, int n)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BingImageUrl.Archive("www.bing.com", idx, n, "en-US"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 8)]
    [InlineData(3, 8)]
    public void Archive_BoundaryIdxAndN_Accepted(int idx, int n)
    {
        Uri url = BingImageUrl.Archive("www.bing.com", idx, n, "de-DE");

        Assert.Equal($"/HPImageArchive.aspx?format=js&idx={idx}&n={n}&mkt=de-DE", url.PathAndQuery);
    }

    [Fact]
    public void MinDimensions_Uhd_Is3840x2160()
    {
        Assert.Equal((3840, 2160), BingImageUrl.MinDimensions("UHD"));
        Assert.Equal((1920, 1200), BingImageUrl.MinDimensions("1920x1200"));
        Assert.Equal((1920, 1080), BingImageUrl.MinDimensions("1920x1080"));
        Assert.Throws<ArgumentOutOfRangeException>(() => BingImageUrl.MinDimensions("4K"));
    }

    [Fact]
    public void CatalogUrls_PointAtRawGitHubOnly()
    {
        Assert.Equal("https://raw.githubusercontent.com/niumoo/bing-wallpaper/main/README.md", BingImageUrl.CatalogReadme().AbsoluteUri);
        Assert.EndsWith("/picture/2026-09/README.md", BingImageUrl.CatalogMonth(2026, 9).AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("raw.githubusercontent.com", BingImageUrl.CatalogMonth(2026, 9).Host);
        Assert.Throws<ArgumentOutOfRangeException>(() => BingImageUrl.CatalogMonth(2026, 13));
    }
}
