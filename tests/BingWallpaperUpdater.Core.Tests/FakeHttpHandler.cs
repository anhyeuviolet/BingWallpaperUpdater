using System.Net;
using System.Net.Http.Headers;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// Scripted <see cref="HttpMessageHandler"/> so no test ever opens a socket (NFR-04). Two modes:
/// a single responder function, or routes keyed by (host, path prefix) added with <see cref="Map"/>.
/// Every request is recorded with its headers so tests can assert on User-Agent, If-None-Match, Cookie and
/// the exact sequence of hosts/paths that the gateway tried.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _respond;
    private readonly List<(string Host, string PathPrefix, Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond)> _routes = new();

    public FakeHttpHandler()
    {
    }

    public FakeHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this(r => Task.FromResult(respond(r)))
    {
    }

    /// <summary>Every request the gateway handed to this handler, in order.</summary>
    public List<RecordedRequest> Requests { get; } = new();

    public IEnumerable<RecordedRequest> RequestsTo(string host) =>
        Requests.Where(r => string.Equals(r.Uri.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a route; the first route whose host matches and whose path prefix matches wins.</summary>
    public FakeHttpHandler Map(string host, string pathPrefix, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((host, pathPrefix, r => Task.FromResult(respond(r))));
        return this;
    }

    public FakeHttpHandler Map(string host, string pathPrefix, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        _routes.Add((host, pathPrefix, respond));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(RecordedRequest.From(request));

        if (_respond is not null)
        {
            return await _respond(request).ConfigureAwait(false);
        }

        Uri uri = request.RequestUri ?? throw new UnroutedRequestException("request without a URI");
        foreach (var route in _routes)
        {
            if (string.Equals(route.Host, uri.Host, StringComparison.OrdinalIgnoreCase)
                && uri.PathAndQuery.StartsWith(route.PathPrefix, StringComparison.Ordinal))
            {
                return await route.Respond(request).ConfigureAwait(false);
            }
        }

        throw new UnroutedRequestException($"no fake route for {request.Method} {uri}");
    }

    // ---- response builders -------------------------------------------------------------------

    public static HttpResponseMessage Text(int status, string body, string? etag = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain"),
        };
        if (etag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        return response;
    }

    public static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage Bytes(int status, string contentType, byte[] body, long? contentLengthOverride = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (contentLengthOverride is long len)
        {
            content.Headers.ContentLength = len;
        }

        return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
    }

    public static HttpResponseMessage Stream(int status, string contentType, Stream body, long? contentLength)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (contentLength is long len)
        {
            content.Headers.ContentLength = len;
        }

        return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
    }

    public static HttpResponseMessage Status(int status)
    {
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(Array.Empty<byte>()) };
    }

    public static Func<HttpRequestMessage, HttpResponseMessage> Throw(Exception exception) => _ => throw exception;

    /// <summary>A stream that serves <paramref name="prefix"/> and then throws <see cref="IOException"/>.</summary>
    public static Stream StreamThatThrowsAfter(byte[] prefix, int bytes) => new ThrowingStream(prefix, bytes);

    /// <summary>A stream whose every read throws — proves the body was never touched.</summary>
    public static Stream StreamThatMustNotBeRead() => new ThrowingStream(Array.Empty<byte>(), 0, throwOnFirstRead: true);

    /// <summary>A stream that yields <paramref name="totalBytes"/> of filler without allocating it.</summary>
    public static Stream EndlessStream(long totalBytes) => new FillerStream(totalBytes);

    private sealed class ThrowingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly int _limit;
        private readonly bool _throwOnFirstRead;
        private int _position;

        public ThrowingStream(byte[] prefix, int limit, bool throwOnFirstRead = false)
        {
            _prefix = prefix;
            _limit = Math.Min(limit, prefix.Length);
            _throwOnFirstRead = throwOnFirstRead;
        }

        public bool WasRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            WasRead = true;
            if (_throwOnFirstRead)
            {
                throw new InvalidOperationException("response body must not be read");
            }

            if (_position >= _limit)
            {
                throw new IOException("simulated connection reset");
            }

            int n = Math.Min(buffer.Length, _limit - _position);
            _prefix.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FillerStream : Stream
    {
        private readonly long _total;
        private long _position;

        public FillerStream(long total)
        {
            _total = total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = _total - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int n = (int)Math.Min(buffer.Length, remaining);
            buffer[..n].Clear();
            if (_position == 0 && n >= 2)
            {
                buffer[0] = 0xFF;
                buffer[1] = 0xD8;
            }

            _position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Thrown (and deliberately not an <see cref="HttpRequestException"/>) when a test forgot a route.</summary>
public sealed class UnroutedRequestException : Exception
{
    public UnroutedRequestException(string message)
        : base(message)
    {
    }
}

/// <summary>Snapshot of one request as the handler saw it (method, URI, and all headers joined by ", ").</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers)
{
    public static RecordedRequest From(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            headers[h.Key] = string.Join(", ", h.Value);
        }

        return new RecordedRequest(request.Method, request.RequestUri!, headers);
    }

    public string? Header(string name) => Headers.TryGetValue(name, out string? v) ? v : null;
}
