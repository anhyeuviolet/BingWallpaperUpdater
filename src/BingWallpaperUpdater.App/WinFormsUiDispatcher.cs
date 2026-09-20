using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Ports;

namespace BingWallpaperUpdater.App;

/// <summary>
/// <see cref="IUiDispatcher"/> over the WinForms <see cref="SynchronizationContext"/> captured on the UI thread
/// (RESEARCH Pattern 4). <c>Post</c> queues the delegate to the message loop and a <see cref="TaskCompletionSource{T}"/>
/// carries the result back; <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> keeps the tick's
/// continuation off the UI thread. The delegate runs only when the token is still live, and its exception is
/// marshalled as a faulted task rather than swallowed (T-02-05). The UI thread never waits on the tick, and the tick
/// never blocks on the UI thread, so the two cannot deadlock.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class WinFormsUiDispatcher(SynchronizationContext ui) : IUiDispatcher
{
    private readonly SynchronizationContext _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(func);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);

        return tcs.Task;
    }
}
