namespace BingWallpaperUpdater.Core.Net;

/// <summary>Bounded retry with jittered backoff and Retry-After. Implemented in the GREEN step of Plan 02 Task 2.</summary>
public sealed class RetryPolicy
{
    public RetryPolicy(int maxAttempts = 3, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
    {
    }

    public Task<HttpResponseMessage> ExecuteAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct, string? host = null) =>
        throw new NotImplementedException();

    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response, TimeSpan cap) => throw new NotImplementedException();
}
