namespace BingWallpaperUpdater.Core.Net;

/// <summary>
/// The only hosts this process may talk to (SRC-10, PROJECT.md "Network"). HTTPS only; enforced by the
/// single send path in <see cref="HttpGateway"/> before any socket is opened.
/// </summary>
public static class HostAllowList
{
    public static readonly IReadOnlySet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "raw.githubusercontent.com",
        "www.bing.com",
        "cn.bing.com",
    };

    public static bool IsAllowed(Uri url) =>
        url is { IsAbsoluteUri: true }
        && string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && Hosts.Contains(url.Host);
}
