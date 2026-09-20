using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using BingWallpaperUpdater.Core.Cache;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;

namespace BingWallpaperUpdater.Core.Net;

public sealed record TextResponse(int Status, string? Body, string? ETag);

public sealed record DownloadResult(bool Ok, string Reason, long Bytes, int Width, int Height, string? Path);

/// <summary>
/// The one place requests leave the process. Every request passes <see cref="HostAllowList.IsAllowed"/>
/// before a socket is opened — outside the retry loop and again on the single send path; cookies and
/// redirects are off so no Microsoft identifier persists and no 3xx can leave the allow-list (threats
/// T-01-01, T-01-05, T-01-11). Transport failures and 5xx go through <see cref="RetryPolicy"/> (3 attempts,
/// jittered 1 s / 3 s backoff, Retry-After) and a request to <c>www.bing.com</c> that still fails is retried
/// once on <c>cn.bing.com</c> with the identical path (SRC-06). Image bodies are streamed straight to a
/// <c>.part</c> file under a 64 MB cap and validated (status, length, JPEG SOF dimensions) before the atomic
/// rename — a UHD JPEG is never held as a byte[] (NFR-03, PITFALLS P1/P12, T-01-03).
/// </summary>
public sealed class HttpGateway : IDisposable
{
    /// <summary>Largest body ever accepted; a UHD Bing JPEG is 0.6-3.6 MB, so this is a DoS guard, not a limit.</summary>
    public const long MaxBodyBytes = 64 * 1024 * 1024;

    private const int CopyBufferSize = 1 << 16;
    private readonly HttpClient _client;
    private readonly RetryPolicy _retry;

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
        _retry = retry ?? new RetryPolicy();
    }

    /// <summary>
    /// GET a text body. Sends <c>If-None-Match</c> when <paramref name="ifNoneMatch"/> is set; the body is
    /// null on 304 and on any non-200 status. Throws <see cref="InvalidOperationException"/> for a host outside
    /// the allow-list before any handler call, and the final transport exception once retries are spent.
    /// </summary>
    public async Task<TextResponse> GetTextAsync(Uri url, string? ifNoneMatch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!HostAllowList.IsAllowed(url))
        {
            throw new InvalidOperationException($"host not allowed: {url.Host}");
        }

        using HttpResponseMessage response = await SendWithFailoverAsync(url, ifNoneMatch, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        string? etag = response.Headers.ETag?.ToString();
        string? body = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : null;

        LogHttp(response.RequestMessage?.RequestUri ?? url, status, body is null ? 0 : response.Content.Headers.ContentLength ?? body.Length);
        return new TextResponse(status, body, etag);
    }

    /// <summary>
    /// Streams a JPEG to <paramref name="finalPath"/> via <c>finalPath + ".part"</c>. Validation order is fixed:
    /// host allow-list, destination inside <see cref="DownloadRoot"/>, HTTP status (the CDN's 404 is a real JPEG —
    /// its body is never read), <c>Content-Length</c> against the 64 MB cap, bytes written against the cap and
    /// against <c>Content-Length</c>, JPEG SOF present, dimensions at least the minimum. The current wallpaper is
    /// left untouched on every rejection because no file ever reaches <paramref name="finalPath"/> and the
    /// <c>.part</c> file is always removed.
    /// </summary>
    public async Task<DownloadResult> DownloadJpegAsync(Uri url, string finalPath, int minWidth, int minHeight, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrEmpty(finalPath);
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
            using HttpResponseMessage response = await SendWithFailoverAsync(url, null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            Uri served = response.RequestMessage?.RequestUri ?? url;

            int status = (int)response.StatusCode;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogHttp(served, status, 0);
                string contentType = response.Content.Headers.ContentType?.MediaType ?? "-";
                return Fail($"status {status} content-type {contentType}");
            }

            long? expected = response.Content.Headers.ContentLength;
            if (expected is long declared && declared > MaxBodyBytes)
            {
                LogHttp(served, status, 0);
                return Fail($"too large content-length={declared}");
            }

            long written = 0;
            bool tooLarge = false;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                await using Stream src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous);
                int n;
                while ((n = await src.ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct).ConfigureAwait(false)) > 0)
                {
                    written += n;
                    if (written > MaxBodyBytes)
                    {
                        tooLarge = true;
                        break;
                    }

                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }

                await dst.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            LogHttp(served, status, written);

            if (tooLarge)
            {
                return Fail($"too large written>{MaxBodyBytes}");
            }

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

    // ---- send pipeline: failover -> retry -> single send path --------------------------------------------

    /// <summary>
    /// Runs the retried send; when the host is <see cref="BingImageUrl.PrimaryHost"/> and the outcome is still a
    /// transport failure or a 5xx, rebuilds the URL on <see cref="BingImageUrl.RetryHost"/> (same scheme, path
    /// and query — never a host taken from a response) and runs the policy once more (T-01-11).
    /// </summary>
    private async Task<HttpResponseMessage> SendWithFailoverAsync(Uri url, string? ifNoneMatch, HttpCompletionOption completion, CancellationToken ct)
    {
        bool canFailOver = string.Equals(url.Host, BingImageUrl.PrimaryHost, StringComparison.OrdinalIgnoreCase);
        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(url, ifNoneMatch, completion, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (canFailOver && !ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Warn($"failover host={BingImageUrl.RetryHost} reason={ex.GetType().Name}: {ex.Message}");
            return await SendWithRetryAsync(FailoverUrl(url), ifNoneMatch, completion, ct).ConfigureAwait(false);
        }

        if (canFailOver && (int)response.StatusCode is >= 500 and <= 599)
        {
            Log.Warn($"failover host={BingImageUrl.RetryHost} reason=status {(int)response.StatusCode}");
            response.Dispose();
            return await SendWithRetryAsync(FailoverUrl(url), ifNoneMatch, completion, ct).ConfigureAwait(false);
        }

        return response;
    }

    private Task<HttpResponseMessage> SendWithRetryAsync(Uri url, string? ifNoneMatch, HttpCompletionOption completion, CancellationToken ct) =>
        _retry.ExecuteAsync(token => SendOnceAsync(url, ifNoneMatch, completion, token), ct, url.Host);

    /// <summary>The single send path: a fresh request per attempt, allow-list first, then the socket.</summary>
    private async Task<HttpResponseMessage> SendOnceAsync(Uri url, string? ifNoneMatch, HttpCompletionOption completion, CancellationToken ct)
    {
        if (!HostAllowList.IsAllowed(url))
        {
            throw new InvalidOperationException($"host not allowed: {url.Host}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(ifNoneMatch) && EntityTagHeaderValue.TryParse(ifNoneMatch, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        return await _client.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    private static Uri FailoverUrl(Uri url) => new UriBuilder(url) { Host = BingImageUrl.RetryHost }.Uri;

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
