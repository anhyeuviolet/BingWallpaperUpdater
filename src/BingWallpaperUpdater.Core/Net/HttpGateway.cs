using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Diagnostics;

namespace BingWallpaperUpdater.Core.Net;

public sealed record TextResponse(int Status, string? Body, string? ETag);

public sealed record DownloadResult(bool Ok, string Reason, long Bytes, int Width, int Height, string? Path);

/// <summary>
/// The one place requests leave the process. Every request passes <see cref="HostAllowList.IsAllowed"/>
/// before a socket is opened; cookies and redirects are off so no Microsoft identifier persists and no
/// 3xx can leave the allow-list (threats T-01-01, T-01-05, T-01-11). Image bodies are streamed straight
/// to a <c>.part</c> file and validated (status, length, JPEG SOF dimensions) before the atomic rename —
/// a UHD JPEG is never held as a byte[] (NFR-03, PITFALLS P1/P12).
/// </summary>
public sealed class HttpGateway : IDisposable
{
    private const int CopyBufferSize = 1 << 16;
    private readonly HttpClient _client;

    /// <summary>
    /// The only directory tree <see cref="DownloadJpegAsync"/> may write into (defence in depth, T-01-02).
    /// Defaults to <see cref="Io.AppPaths.CacheDir"/>; tests point it at a temp directory.
    /// </summary>
    public string DownloadRoot { get; set; } = Io.AppPaths.CacheDir;

    public HttpGateway(HttpMessageHandler? handler = null, string? userAgent = null, RetryPolicy? retry = null)
    {
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? DefaultUserAgent());
    }

    /// <summary>
    /// GET a text body. Sends <c>If-None-Match</c> when <paramref name="ifNoneMatch"/> is set; the body is
    /// null on 304 and on any non-200 status.
    /// </summary>
    public async Task<TextResponse> GetTextAsync(Uri url, string? ifNoneMatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(ifNoneMatch) && EntityTagHeaderValue.TryParse(ifNoneMatch, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        string? etag = response.Headers.ETag?.ToString();
        string? body = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : null;

        LogHttp(url, status, body is null ? 0 : response.Content.Headers.ContentLength ?? body.Length);
        return new TextResponse(status, body, etag);
    }

    /// <summary>
    /// Streams a JPEG to <paramref name="finalPath"/> via <c>finalPath + ".part"</c>. Rejects: disallowed host,
    /// a destination outside <see cref="DownloadRoot"/>, any status other than 200 (the CDN's 404 is a real JPEG — never read its body), a body shorter than
    /// <c>Content-Length</c>, a non-JPEG, or dimensions below the minimum. The current wallpaper is left
    /// untouched on every rejection because no file ever reaches <paramref name="finalPath"/>.
    /// </summary>
    public async Task<DownloadResult> DownloadJpegAsync(Uri url, string finalPath, int minWidth, int minHeight, CancellationToken ct)
    {
        if (!HostAllowList.IsAllowed(url))
        {
            return Fail($"host not allowed: {url.Host}");
        }

        string fullFinal = Path.GetFullPath(finalPath);
        string cacheRoot = Path.GetFullPath(DownloadRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullFinal.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("destination outside cache directory");
        }

        string part = fullFinal + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogHttp(url, status, 0);
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "-";
                return Fail($"status {status} content-type {contentType}");
            }

            long? expected = response.Content.Headers.ContentLength;
            long written;
            await using (Stream src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
            {
                await src.CopyToAsync(dst, CopyBufferSize, ct).ConfigureAwait(false);
                await dst.FlushAsync(ct).ConfigureAwait(false);
                written = dst.Length;
            }

            LogHttp(url, status, written);

            if (expected is long e && e != written)
            {
                return Fail($"truncated expected={e} written={written}");
            }

            (int Width, int Height)? dims;
            using (FileStream fs = File.OpenRead(part))
            {
                dims = JpegHeader.ReadDimensions(fs);
            }

            if (dims is null)
            {
                return Fail("not a JPEG");
            }

            if (dims.Value.Width < minWidth || dims.Value.Height < minHeight)
            {
                return Fail($"too small {dims.Value.Width}x{dims.Value.Height}");
            }

            File.Move(part, fullFinal, overwrite: true);
            return new DownloadResult(true, "ok", written, dims.Value.Width, dims.Value.Height, fullFinal);
        }
        catch (HttpRequestException ex)
        {
            return Fail($"request failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Fail($"io failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("timeout");
        }
        finally
        {
            try
            {
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The single send path: allow-list first, then the socket.</summary>
    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        Uri? url = request.RequestUri;
        if (url is null || !HostAllowList.IsAllowed(url))
        {
            throw new InvalidOperationException($"host not allowed: {url?.Host ?? "<null>"}");
        }

        return _client.SendAsync(request, completion, ct);
    }

    private static void LogHttp(Uri url, int status, long bytes) =>
        Log.Info($"http {url.Host} {status} {bytes}");

    private static DownloadResult Fail(string reason) => new(false, reason, 0, 0, 0, null);

    private static string DefaultUserAgent()
    {
        Assembly asm = typeof(HttpGateway).Assembly;
        string version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            version = version[..plus];
        }

        string? repo = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "RepositoryUrl", StringComparison.Ordinal))?.Value;

        return string.IsNullOrWhiteSpace(repo)
            ? $"BingWallpaperUpdater/{version}"
            : $"BingWallpaperUpdater/{version} (+{repo})";
    }

    public void Dispose() => _client.Dispose();
}
