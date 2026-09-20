using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using BingWallpaperUpdater.Core.Diagnostics;

namespace BingWallpaperUpdater.Core.Net;

/// <summary>
/// The 20-line resilience loop RESEARCH "Don't Hand-Roll" prescribes instead of Polly: at most
/// <c>maxAttempts</c> sends; transport failures (<see cref="HttpRequestException"/>, <see cref="IOException"/>,
/// the per-request timeout) and 5xx responses are retried after a jittered backoff of 1 s / 3 s / 9 s (±30 %);
/// a <c>Retry-After</c> header replaces the backoff when present and at most <see cref="RetryAfterCap"/>.
/// 429 is governed by <c>Retry-After</c> alone: one retry when the header is usable, otherwise the 429 is
/// returned immediately so the caller can switch sources without sleeping (PITFALLS P6, T-01-03, T-01-14).
/// Other 4xx and every 3xx are returned as-is; the caller's own cancellation is never retried.
/// </summary>
public sealed class RetryPolicy
{
    public const int DefaultMaxAttempts = 3;
    public static readonly TimeSpan RetryAfterCap = TimeSpan.FromSeconds(60);

    private const double JitterFraction = 0.3;
    private static readonly TimeSpan[] BaseDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(9),
    };

    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _random;

    /// <param name="maxAttempts">Total sends including the first; 3 by default.</param>
    /// <param name="delay">Sleep function; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. Tests inject a recorder.</param>
    /// <param name="random">Jitter source; defaults to <see cref="Random.Shared"/>.</param>
    public RetryPolicy(int maxAttempts = DefaultMaxAttempts, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _maxAttempts = maxAttempts;
        _delay = delay ?? Task.Delay;
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// Runs <paramref name="send"/> until it yields a non-retryable response or the attempt budget is spent.
    /// The final failing response is returned (never thrown); the final transport exception is rethrown.
    /// <paramref name="send"/> must create a fresh <see cref="HttpRequestMessage"/> on every call.
    /// </summary>
    public async Task<HttpResponseMessage> ExecuteAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct, string? host = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        bool retryAfterUsed = false;

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            ExceptionDispatchInfo? failure = null;
            try
            {
                response = await send(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            TimeSpan wait;
            if (response is not null)
            {
                int status = (int)response.StatusCode;
                if (status == 429)
                {
                    TimeSpan? retryAfter = ParseRetryAfter(response, RetryAfterCap);
                    if (retryAfter is null || retryAfterUsed || attempt >= _maxAttempts)
                    {
                        return response;
                    }

                    retryAfterUsed = true;
                    wait = retryAfter.Value;
                }
                else if (status is < 500 or > 599 || attempt >= _maxAttempts)
                {
                    return response;
                }
                else
                {
                    wait = ParseRetryAfter(response, RetryAfterCap) ?? Backoff(attempt);
                }

                response.Dispose();
            }
            else
            {
                if (attempt >= _maxAttempts)
                {
                    failure!.Throw();
                }

                wait = Backoff(attempt);
            }

            Log.Warn($"retry attempt={attempt + 1} host={host ?? "-"} delay={(long)wait.TotalMilliseconds}");
            await _delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>Retry-After</c> as a delay: delta-seconds or an HTTP-date relative to now (never negative). Null when
    /// the header is absent, unparsable, or longer than <paramref name="cap"/> — the caller then treats the
    /// response as terminal instead of sleeping.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response, TimeSpan cap)
    {
        ArgumentNullException.ThrowIfNull(response);
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        TimeSpan? value = header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (value is not { } delay)
        {
            return null;
        }

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay <= cap ? delay : null;
    }

    /// <summary>Delay before attempt <c>completedAttempt + 1</c>: base × (1 ± 0.3 · random).</summary>
    private TimeSpan Backoff(int completedAttempt)
    {
        TimeSpan baseDelay = BaseDelays[Math.Min(completedAttempt, BaseDelays.Length) - 1];
        double factor = 1.0 + JitterFraction * (2.0 * _random.NextDouble() - 1.0);
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    /// <summary>
    /// Transport-level failures worth another attempt; the caller's cancellation is not one, and neither is a
    /// response that exceeded a configured limit (<see cref="HttpRequestError.ConfigurationLimitExceeded"/> — the
    /// text buffer cap): the same oversized body would come back on every retry and on the mirror.
    /// </summary>
    internal static bool IsTransient(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested
        && ex is HttpRequestException { HttpRequestError: not HttpRequestError.ConfigurationLimitExceeded }
            or IOException
            or TaskCanceledException;
}
