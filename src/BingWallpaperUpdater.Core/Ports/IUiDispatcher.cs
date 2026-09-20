namespace BingWallpaperUpdater.Core.Ports;

/// <summary>
/// Port for the awaitable hop onto the UI (STA) thread, implemented by the App adapter <c>WinFormsUiDispatcher</c>
/// (RESEARCH Pattern 4). The tick awaits the result so it knows whether the COM apply succeeded before it records
/// <c>LastSeenNewestId</c> and re-arms (D-03, Pitfall 6). Tests use an inline dispatcher that runs the delegate
/// synchronously. Core never touches WinForms.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="func"/> on the UI thread; the task completes with its result, faults with its exception, or is cancelled if <paramref name="ct"/> was signalled before it ran.</summary>
    Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct);
}
