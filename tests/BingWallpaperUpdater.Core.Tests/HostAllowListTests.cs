using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>SRC-10: HTTPS only, exactly three hosts, no subdomain or suffix tricks.</summary>
public sealed class HostAllowListTests
{
    [Theory]
    [InlineData("https://raw.githubusercontent.com/x")]
    [InlineData("https://raw.githubusercontent.com/niumoo/bing-wallpaper/main/README.md")]
    [InlineData("https://WWW.BING.COM/th")]
    [InlineData("https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=en-US")]
    [InlineData("https://cn.bing.com/th")]
    [InlineData("https://cn.bing.com:443/th?id=OHR.X_EN-US1_UHD.jpg")]
    public void IsAllowed_AllowListedHttpsHosts_ReturnTrue(string url)
    {
        Assert.True(HostAllowList.IsAllowed(new Uri(url)));
    }

    [Theory]
    [InlineData("http://www.bing.com/th")]
    [InlineData("http://raw.githubusercontent.com/x")]
    [InlineData("https://bing.com/th")]
    [InlineData("https://www.bing.com.evil.com/th")]
    [InlineData("https://evil.com/www.bing.com/th")]
    [InlineData("https://example.com")]
    [InlineData("https://github.com/niumoo/bing-wallpaper")]
    [InlineData("https://raw.githubusercontent.com.attacker.net/x")]
    [InlineData("ftp://www.bing.com/th")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void IsAllowed_OtherSchemesOrHosts_ReturnFalse(string url)
    {
        Assert.False(HostAllowList.IsAllowed(new Uri(url)));
    }

    [Fact]
    public void IsAllowed_RelativeUri_ReturnsFalse()
    {
        Assert.False(HostAllowList.IsAllowed(new Uri("/th?id=x", UriKind.Relative)));
    }

    [Fact]
    public void Hosts_IsExactlyTheThreeDocumentedHosts()
    {
        Assert.Equal(3, HostAllowList.Hosts.Count);
        Assert.Contains("raw.githubusercontent.com", HostAllowList.Hosts);
        Assert.Contains("www.bing.com", HostAllowList.Hosts);
        Assert.Contains("cn.bing.com", HostAllowList.Hosts);
    }

    [Fact]
    public void IsAllowed_UserInfoInUrl_StillJudgedByHost()
    {
        Assert.False(HostAllowList.IsAllowed(new Uri("https://www.bing.com@evil.com/th")));
        Assert.True(HostAllowList.IsAllowed(new Uri("https://user@www.bing.com/th")));
    }
}
