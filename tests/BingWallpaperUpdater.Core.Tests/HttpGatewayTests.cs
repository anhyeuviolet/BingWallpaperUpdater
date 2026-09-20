using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// Every negative CDN path, the retry policy, Retry-After, www→cn failover, the 64 MB cap, conditional GET and
/// request hygiene — all through <see cref="FakeHttpHandler"/>, never a socket (NFR-04). Joins the "LogSink"
/// collection because the retry tests assert on the process-global <see cref="Log"/>.
/// </summary>
[Collection("LogSink")]
public sealed class HttpGatewayTests : IDisposable
{
    private const string Www = "www.bing.com";
    private const string Cn = "cn.bing.com";
    private const string GitHub = "raw.githubusercontent.com";
    private static readonly Uri UhdUrl = new($"https://{Www}/th?id=OHR.AlphornBavaria_EN-US6200857270_UHD.jpg");
    private static readonly Uri ReadmeUrl = new($"https://{GitHub}/niumoo/bing-wallpaper/main/README.md");

    private readonly string _dir;
    private readonly string _finalPath;
    private readonly string _logPath;

    public HttpGatewayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bwu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _finalPath = Path.Combine(_dir, "2026-09-20_AlphornBavaria.jpg");
        _logPath = Path.Combine(_dir, "log.txt");
        Log.Initialize(_logPath);
    }

    public void Dispose()
    {
        Log.Initialize(Path.Combine(_dir, "closed.log"));
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LogText() => File.Exists(_logPath) ? File.ReadAllText(_logPath) : string.Empty;

    private HttpGateway Gateway(FakeHttpHandler fake, RetryPolicy? retry = null) =>
        new(fake, retry: retry ?? NoDelayPolicy()) { DownloadRoot = _dir };

    /// <summary>A policy whose delays are recorded instead of slept.</summary>
    private static RetryPolicy RecordingPolicy(List<TimeSpan> delays) =>
        new(delay: (d, _) => { delays.Add(d); return Task.CompletedTask; }, random: new Random(20260920));

    private static RetryPolicy NoDelayPolicy() => RecordingPolicy(new List<TimeSpan>());

    private static byte[] Placeholder404() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "placeholder-404.jpg"));

    private void AssertNoFilesRemain()
    {
        Assert.False(File.Exists(_finalPath), "final file must not exist");
        Assert.False(File.Exists(_finalPath + ".part"), ".part file must not remain");
        Assert.Empty(Directory.GetFiles(_dir, "*.jpg*"));
    }

    /// <summary>Synthesises a header-only JPEG: SOI, SOF0 with the given dimensions, EOI.</summary>
    private static class JpegBytes
    {
        public static byte[] Sof0(int width, int height)
        {
            var bytes = new List<byte> { 0xFF, 0xD8 };
            // APP0 (JFIF) so the header walker has a non-SOF segment to skip first.
            bytes.AddRange(new byte[] { 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 });
            bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
            bytes.Add((byte)(height >> 8));
            bytes.Add((byte)(height & 0xFF));
            bytes.Add((byte)(width >> 8));
            bytes.Add((byte)(width & 0xFF));
            bytes.Add(0x03);
            bytes.AddRange(new byte[] { 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });
            bytes.AddRange(new byte[] { 0xFF, 0xD9 });
            return bytes.ToArray();
        }
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    [Fact]
    public void Fixture_Placeholder404_IsARealJpegBody()
    {
        byte[] body = Placeholder404();

        Assert.InRange(body.Length, 500, 5000);
        Assert.Equal(0xFF, body[0]);
        Assert.Equal(0xD8, body[1]);
    }

    // ---- download validation (SRC-08) -------------------------------------------------------------

    [Fact]
    public async Task Download_Status404WithJpegBody_RejectedWithoutReadingBodyOrCreatingFiles()
    {
        Stream body = FakeHttpHandler.StreamThatMustNotBeRead();
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(404, "image/jpeg", body, Placeholder404().Length));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("status 404", result.Reason, StringComparison.Ordinal);
        Assert.Single(fake.Requests);
        AssertNoFilesRemain();
    }

    [Fact]
    public async Task Download_Truncated_OneByteShortOfContentLength_RejectedNoFilesRemain()
    {
        byte[] nine = JpegBytes.Sof0(3840, 2160)[..9];
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(200, "image/jpeg", nine, contentLengthOverride: 10));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("truncated", result.Reason, StringComparison.Ordinal);
        AssertNoFilesRemain();
    }

    [Fact]
    public async Task Download_ContentLengthEqualsBody_ProceedsToJpegCheckAndSucceeds()
    {
        byte[] jpeg = JpegBytes.Sof0(3840, 2160);
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(200, "image/jpeg", jpeg, contentLengthOverride: jpeg.Length));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(jpeg.Length, result.Bytes);
        Assert.True(File.Exists(_finalPath));
        Assert.False(File.Exists(_finalPath + ".part"));
    }

    [Fact]
    public async Task Download_EmptyBody_NotJpegNoFilesRemain()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(200, "image/jpeg", Array.Empty<byte>()));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("not a JPEG", result.Reason);
        AssertNoFilesRemain();
    }

    [Fact]
    public async Task Download_NotJpeg_RandomBytesRejected()
    {
        var random = new byte[2000];
        new Random(7).NextBytes(random);
        random[0] = 0x00;
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(200, "image/jpeg", random));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("not a JPEG", result.Reason);
        AssertNoFilesRemain();
    }

    [Fact]
    public async Task Download_Sof3840x2160_AcceptedWithExactDimensions()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(3840, 2160)));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(3840, result.Width);
        Assert.Equal(2160, result.Height);
        Assert.Equal(_finalPath, result.Path);
        Assert.True(File.Exists(_finalPath));
        Assert.False(File.Exists(_finalPath + ".part"));
    }

    [Theory]
    [InlineData(3839, 2160)]
    [InlineData(3840, 2159)]
    [InlineData(384, 216)]
    public async Task Download_TooSmall_OnePixelUnderMinimum_Rejected(int width, int height)
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(width, height)));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("too small", result.Reason, StringComparison.Ordinal);
        AssertNoFilesRemain();
    }

    [Fact]
    public async Task Download_Redirect302_NotFollowedSingleRequest()
    {
        var fake = new FakeHttpHandler(_ =>
        {
            HttpResponseMessage r = FakeHttpHandler.Status(302);
            r.Headers.Location = new Uri($"https://{Cn}/th?id=OHR.AlphornBavaria_EN-US6200857270_UHD.jpg");
            return r;
        });

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("status 302", result.Reason, StringComparison.Ordinal);
        Assert.Single(fake.Requests);
        AssertNoFilesRemain();
    }

    [Theory]
    [InlineData("https://example.com/th?id=OHR.X_EN-US1_UHD.jpg")]
    [InlineData("http://www.bing.com/th?id=OHR.X_EN-US1_UHD.jpg")]
    [InlineData("https://www.bing.com.evil.com/th?id=OHR.X_EN-US1_UHD.jpg")]
    public async Task Download_HostNotAllowed_ZeroRequestsReachTheHandler(string url)
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(3840, 2160)));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(new Uri(url), _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("host not allowed: ", result.Reason, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
        AssertNoFilesRemain();
    }

    [Fact]
    public async Task GetText_HostNotAllowed_ThrowsBeforeAnyHandlerCall()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Text(200, "x"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Gateway(fake).GetTextAsync(new Uri("https://example.com/README.md"), null, CancellationToken.None));

        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Download_InterruptedStream_NoPartAndNoFinalFileRemain()
    {
        byte[] jpeg = JpegBytes.Sof0(3840, 2160);
        var prefix = new byte[200];
        jpeg.CopyTo(prefix, 0);
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(200, "image/jpeg", FakeHttpHandler.StreamThatThrowsAfter(prefix, 100), contentLength: null));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        AssertNoFilesRemain();
    }

    /// <summary>WR-02: a body that stops arriving after the headers is abandoned after <see cref="HttpGateway.BodyReadTimeout"/>.</summary>
    [Fact]
    public async Task Download_BodyStallsAfterHeaders_FailsWithTimeoutInsteadOfHangingAndLeavesNoFiles()
    {
        byte[] prefix = JpegBytes.Sof0(3840, 2160)[..12];
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(200, "image/jpeg", FakeHttpHandler.StreamThatStallsAfter(prefix), contentLength: 4_000_000));
        using HttpGateway gateway = Gateway(fake);
        gateway.BodyReadTimeout = TimeSpan.FromMilliseconds(200);

        Task<DownloadResult> download = gateway.DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);
        Task finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(download, finished);
        DownloadResult result = await download;
        Assert.False(result.Ok);
        Assert.Equal("timeout", result.Reason);
        Assert.Single(fake.Requests);
        AssertNoFilesRemain();
    }

    /// <summary>The idle timer restarts on every chunk: a slow but live transfer longer than the timeout still succeeds.</summary>
    [Fact]
    public async Task Download_SlowButLiveBody_EachChunkInsideTheIdleWindow_Succeeds()
    {
        byte[] jpeg = JpegBytes.Sof0(3840, 2160);
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(200, "image/jpeg", FakeHttpHandler.StreamThatTrickles(jpeg, chunk: 8, delay: TimeSpan.FromMilliseconds(60)), contentLength: jpeg.Length));
        using HttpGateway gateway = Gateway(fake);
        gateway.BodyReadTimeout = TimeSpan.FromMilliseconds(250);

        DownloadResult result = await gateway.DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(jpeg.Length, result.Bytes);
    }

    /// <summary>The caller's own cancellation during the body read propagates instead of being reported as a timeout.</summary>
    [Fact]
    public async Task Download_CallerCancelsDuringBody_ThrowsOperationCanceledAndLeavesNoFiles()
    {
        byte[] prefix = JpegBytes.Sof0(3840, 2160)[..12];
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(200, "image/jpeg", FakeHttpHandler.StreamThatStallsAfter(prefix), contentLength: null));
        using HttpGateway gateway = Gateway(fake);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, cts.Token));

        AssertNoFilesRemain();
    }

    [Fact]
    public async Task Download_TooLarge_ContentLengthAboveCap_RejectedBeforeReading()
    {
        Stream body = FakeHttpHandler.StreamThatMustNotBeRead();
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(200, "image/jpeg", body, 70_000_000));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("too large", result.Reason, StringComparison.Ordinal);
        AssertNoFilesRemain();
    }

    [Fact]
    public async Task Download_TooLarge_StreamWithoutContentLength_AbortedAtCap()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(200, "image/jpeg", FakeHttpHandler.EndlessStream(70_000_000), contentLength: null));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.StartsWith("too large", result.Reason, StringComparison.Ordinal);
        AssertNoFilesRemain();
    }

    [Fact]
    public async Task Download_DestinationOutsideDownloadRoot_RejectedWithoutRequest()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(3840, 2160)));
        string outside = Path.Combine(Path.GetTempPath(), "bwu-tests", "outside-" + Guid.NewGuid().ToString("N") + ".jpg");

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, outside, 3840, 2160, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("destination outside cache directory", result.Reason);
        Assert.Empty(fake.Requests);
        Assert.False(File.Exists(outside));
    }

    // ---- failover (SRC-06) ----------------------------------------------------------------------------

    [Fact]
    public async Task Download_Failover_WwwFailsEveryAttempt_CnSucceedsWithIdenticalPath()
    {
        var fake = new FakeHttpHandler()
            .Map(Www, "/", FakeHttpHandler.Throw(new HttpRequestException("www down")))
            .Map(Cn, "/", _ => FakeHttpHandler.Bytes(200, "image/jpeg", JpegBytes.Sof0(3840, 2160)));

        DownloadResult result = await Gateway(fake).DownloadJpegAsync(UhdUrl, _finalPath, 3840, 2160, CancellationToken.None);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(3, fake.RequestsTo(Www).Count());
        RecordedRequest cn = Assert.Single(fake.RequestsTo(Cn));
        Assert.Equal(UhdUrl.PathAndQuery, cn.Uri.PathAndQuery);
        Assert.Equal(4, fake.Requests.Count);
        Assert.Equal(Cn, fake.Requests[^1].Uri.Host);
        Assert.Contains("failover host=cn.bing.com", LogText());
    }

    [Fact]
    public async Task GetText_Failover_Www5xxExhausted_CnAnswers()
    {
        var fake = new FakeHttpHandler()
            .Map(Www, "/", _ => FakeHttpHandler.Status(503))
            .Map(Cn, "/", _ => FakeHttpHandler.Json(200, "{\"images\":[]}"));

        TextResponse response = await Gateway(fake).GetTextAsync(new Uri($"https://{Www}/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=en-US"), null, CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Equal("{\"images\":[]}", response.Body);
        Assert.Equal(3, fake.RequestsTo(Www).Count());
        Assert.Single(fake.RequestsTo(Cn));
    }

    [Fact]
    public async Task GetText_NoFailoverForGitHubHost_ExceptionPropagatesAfterRetries()
    {
        var fake = new FakeHttpHandler(FakeHttpHandler.Throw(new HttpRequestException("github down")));

        await Assert.ThrowsAsync<HttpRequestException>(() => Gateway(fake).GetTextAsync(ReadmeUrl, null, CancellationToken.None));

        Assert.Equal(3, fake.Requests.Count);
        Assert.All(fake.Requests, r => Assert.Equal(GitHub, r.Uri.Host));
    }

    // ---- retry policy ----------------------------------------------------------------------------------

    [Fact]
    public async Task Retry_503Twice_Then200_ThreeRequestsWithJitteredOneAndThreeSecondDelays()
    {
        int calls = 0;
        var fake = new FakeHttpHandler(_ => ++calls < 3 ? FakeHttpHandler.Status(503) : FakeHttpHandler.Text(200, "ok"));
        var delays = new List<TimeSpan>();

        TextResponse response = await Gateway(fake, RecordingPolicy(delays)).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Equal("ok", response.Body);
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(2, delays.Count);
        Assert.InRange(delays[0].TotalSeconds, 0.7, 1.3);
        Assert.InRange(delays[1].TotalSeconds, 2.1, 3.9);
        Assert.Contains("retry attempt=2 host=raw.githubusercontent.com delay=", LogText());
        Assert.Contains("retry attempt=3 host=raw.githubusercontent.com delay=", LogText());
    }

    [Fact]
    public async Task Retry_FourConsecutive503_StopsAfterThreeRequests()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Status(503));
        var delays = new List<TimeSpan>();

        TextResponse response = await Gateway(fake, RecordingPolicy(delays)).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(503, response.Status);
        Assert.Null(response.Body);
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Retry_TransportErrorTwice_ThenSuccess()
    {
        int calls = 0;
        var fake = new FakeHttpHandler(_ => ++calls < 3 ? throw new HttpRequestException("reset") : FakeHttpHandler.Text(200, "ok"));
        var delays = new List<TimeSpan>();

        TextResponse response = await Gateway(fake, RecordingPolicy(delays)).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Retry_PerRequestTimeout_IsRetried_ButCallerCancellationIsNot()
    {
        int calls = 0;
        var fake = new FakeHttpHandler(_ => ++calls < 2
            ? throw new TaskCanceledException("timeout", new TimeoutException())
            : FakeHttpHandler.Text(200, "ok"));

        TextResponse response = await Gateway(fake).GetTextAsync(ReadmeUrl, null, CancellationToken.None);
        Assert.Equal(200, response.Status);
        Assert.Equal(2, fake.Requests.Count);

        using var cts = new CancellationTokenSource();
        Func<HttpRequestMessage, HttpResponseMessage> cancelThenThrow = _ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("cancelled");
        };
        var cancelling = new FakeHttpHandler(cancelThenThrow);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gateway(cancelling).GetTextAsync(ReadmeUrl, null, cts.Token));
        Assert.Single(cancelling.Requests);
    }

    [Fact]
    public async Task Retry_404_NotRetriedSingleRequest()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Status(404));
        var delays = new List<TimeSpan>();

        TextResponse response = await Gateway(fake, RecordingPolicy(delays)).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(404, response.Status);
        Assert.Single(fake.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Retry_429WithRetryAfterTwoSeconds_DelaysExactlyTwoSecondsThenSucceeds()
    {
        int calls = 0;
        var fake = new FakeHttpHandler(_ =>
        {
            if (++calls == 1)
            {
                HttpResponseMessage r = FakeHttpHandler.Status(429);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return r;
            }

            return FakeHttpHandler.Text(200, "ok");
        });
        var delays = new List<TimeSpan>();

        TextResponse response = await Gateway(fake, RecordingPolicy(delays)).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Equal(2, fake.Requests.Count);
        TimeSpan only = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(2), only);
    }

    [Fact]
    public async Task Retry_429WithRetryAfterAboveCap_NoDelayImmediateFailure()
    {
        var fake = new FakeHttpHandler(_ =>
        {
            HttpResponseMessage r = FakeHttpHandler.Status(429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
            return r;
        });
        var delays = new List<TimeSpan>();

        TextResponse response = await Gateway(fake, RecordingPolicy(delays)).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(429, response.Status);
        Assert.Single(fake.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Retry_429WithoutRetryAfter_NoDelaySingleRequestStatusReturned()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Status(429));
        var delays = new List<TimeSpan>();

        TextResponse response = await Gateway(fake, RecordingPolicy(delays)).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(429, response.Status);
        Assert.Null(response.Body);
        Assert.Single(fake.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Retry_503WithRetryAfter_UsesHeaderInsteadOfBackoff()
    {
        int calls = 0;
        var fake = new FakeHttpHandler(_ =>
        {
            if (++calls == 1)
            {
                HttpResponseMessage r = FakeHttpHandler.Status(503);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                return r;
            }

            return FakeHttpHandler.Text(200, "ok");
        });
        var delays = new List<TimeSpan>();

        TextResponse response = await Gateway(fake, RecordingPolicy(delays)).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(delays));
    }

    [Fact]
    public void ParseRetryAfter_SecondsAndHttpDate_RespectCap()
    {
        var cap = TimeSpan.FromSeconds(60);

        var seconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        seconds.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.ParseRetryAfter(seconds, cap));

        var tooLong = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        tooLong.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(61));
        Assert.Null(RetryPolicy.ParseRetryAfter(tooLong, cap));

        var date = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        date.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(20));
        TimeSpan? fromDate = RetryPolicy.ParseRetryAfter(date, cap);
        Assert.NotNull(fromDate);
        Assert.InRange(fromDate.Value.TotalSeconds, 15, 20.5);

        var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.ParseRetryAfter(past, cap));

        Assert.Null(RetryPolicy.ParseRetryAfter(new HttpResponseMessage(HttpStatusCode.OK), cap));
    }

    // ---- conditional GET (SRC-09) ---------------------------------------------------------------------

    [Fact]
    public async Task GetText_IfNoneMatch_SentQuoted_NotModifiedYieldsNullBodyAndResponseETag()
    {
        var fake = new FakeHttpHandler(_ =>
        {
            HttpResponseMessage r = FakeHttpHandler.Status(304);
            r.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
            return r;
        });

        TextResponse response = await Gateway(fake).GetTextAsync(ReadmeUrl, "\"abc\"", CancellationToken.None);

        RecordedRequest request = Assert.Single(fake.Requests);
        Assert.Equal("\"abc\"", request.Header("If-None-Match"));
        Assert.Equal(304, response.Status);
        Assert.Null(response.Body);
        Assert.Equal("\"abc\"", response.ETag);
        Assert.Contains($"http {GitHub} 304 0", LogText());
    }

    [Fact]
    public async Task GetText_200_ReturnsBodyAndETag_NoIfNoneMatchWhenNull()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Text(200, "## Bing Wallpaper", "\"def\""));

        TextResponse response = await Gateway(fake).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Equal("## Bing Wallpaper", response.Body);
        Assert.Equal("\"def\"", response.ETag);
        Assert.Null(Assert.Single(fake.Requests).Header("If-None-Match"));
    }

    // ---- text body cap (WR-03) --------------------------------------------------------------------------

    [Fact]
    public async Task GetText_ContentLengthAboveTextCap_ThrowsHttpRequestException_NoRetryNoFailover()
    {
        Stream body = FakeHttpHandler.StreamThatMustNotBeRead();
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(200, "application/json", body, HttpGateway.MaxTextBytes + 1));
        var delays = new List<TimeSpan>();
        Uri archive = new($"https://{Www}/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=en-US");

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => Gateway(fake, RecordingPolicy(delays)).GetTextAsync(archive, null, CancellationToken.None));

        Assert.Equal(HttpRequestError.ConfigurationLimitExceeded, ex.HttpRequestError);
        Assert.Single(fake.Requests);
        Assert.Empty(fake.RequestsTo(Cn));
        Assert.Empty(delays);
    }

    [Fact]
    public async Task GetText_ChunkedBodyAboveTextCap_AbandonedAtTheCap_SingleRequest()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Stream(200, "text/plain", FakeHttpHandler.EndlessStream(HttpGateway.MaxTextBytes + 1), contentLength: null));

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => Gateway(fake).GetTextAsync(ReadmeUrl, null, CancellationToken.None));

        Assert.Equal(HttpRequestError.ConfigurationLimitExceeded, ex.HttpRequestError);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task GetText_BodyJustUnderTextCap_IsReturned()
    {
        var body = new string('x', 64 * 1024);
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Text(200, body));

        TextResponse response = await Gateway(fake).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal(200, response.Status);
        Assert.Equal(body, response.Body);
    }

    // ---- hygiene (SRC-10, T-01-05) -----------------------------------------------------------------------

    [Fact]
    public async Task Requests_CarryAppUserAgentOnly_AndNeverACookieHeader()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Text(200, "ok"));

        await Gateway(fake).GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        RecordedRequest request = Assert.Single(fake.Requests);
        string? ua = request.Header("User-Agent");
        Assert.NotNull(ua);
        Assert.StartsWith("BingWallpaperUpdater/", ua, StringComparison.Ordinal);
        string withoutComments = Regex.Replace(ua, @"\s*\([^)]*\)", string.Empty);
        string[] products = withoutComments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(products);
        Assert.DoesNotContain(Environment.MachineName, ua, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, ua, StringComparison.OrdinalIgnoreCase);
        Assert.Null(request.Header("Cookie"));
    }

    [Fact]
    public async Task Requests_CustomUserAgent_IsUsedVerbatim()
    {
        var fake = new FakeHttpHandler(_ => FakeHttpHandler.Text(200, "ok"));
        using var gateway = new HttpGateway(fake, "BingWallpaperUpdater/9.9-test", NoDelayPolicy());

        await gateway.GetTextAsync(ReadmeUrl, null, CancellationToken.None);

        Assert.Equal("BingWallpaperUpdater/9.9-test", Assert.Single(fake.Requests).Header("User-Agent"));
    }
}
