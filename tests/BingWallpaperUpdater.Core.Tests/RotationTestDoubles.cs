using BingWallpaperUpdater.Core.Ports;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary><see cref="IUiDispatcher"/> that runs the delegate synchronously on the calling thread (no STA in tests).</summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public int Calls { get; private set; }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct)
    {
        Calls++;
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        try
        {
            return Task.FromResult(func());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }
}

/// <summary>
/// <see cref="IUiDispatcher"/> that parks every delegate until the test calls <see cref="Release"/>, so a tick can be
/// held in flight at its apply step (the Busy / IsTickRunning tests).
/// </summary>
internal sealed class BlockingUiDispatcher : IUiDispatcher
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a delegate has reached the dispatcher and is waiting.</summary>
    public Task Entered => _entered.Task;

    public void Release() => _gate.TrySetResult();

    public async Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct)
    {
        _entered.TrySetResult();
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
        return func();
    }
}

/// <summary>Counting <see cref="IWallpaperApplier"/>; succeeds unless <see cref="FailNext"/> is set for the next call.</summary>
internal sealed class FakeApplier : IWallpaperApplier
{
    public int Calls { get; private set; }
    public string? LastPath { get; private set; }
    public List<string> Paths { get; } = [];
    public bool FailNext { get; set; }

    public void Reset()
    {
        Calls = 0;
        LastPath = null;
        Paths.Clear();
    }

    public ApplyResult Apply(string absolutePath)
    {
        Calls++;
        LastPath = absolutePath;
        Paths.Add(absolutePath);
        if (FailNext)
        {
            FailNext = false;
            return new ApplyResult(false, "fake", null, null, "forced failure");
        }

        return new ApplyResult(true, "fake", absolutePath, "DWPOS_FILL", null);
    }
}
