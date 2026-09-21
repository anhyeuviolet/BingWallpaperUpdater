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

/// <summary><see cref="IMonitorLayout"/> returning a settable list (swap <see cref="Sizes"/> mid-test to simulate a display change); <see cref="Throw"/> makes the next call throw.</summary>
internal sealed class FakeMonitorLayout(IReadOnlyList<(int Width, int Height)> sizes) : IMonitorLayout
{
    public IReadOnlyList<(int Width, int Height)> Sizes { get; set; } = sizes;

    /// <summary>When non-null, every <see cref="IMonitorLayout.Sizes"/> call throws it (the "monitor layout failed" path).</summary>
    public Exception? Throw { get; set; }

    public int Calls { get; private set; }

    IReadOnlyList<(int Width, int Height)> IMonitorLayout.Sizes()
    {
        Calls++;
        if (Throw is { } pending)
        {
            throw pending;
        }

        return Sizes;
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

/// <summary>
/// Counting <see cref="IWallpaperApplier"/>; succeeds unless <see cref="FailNext"/> (a failed <see cref="ApplyResult"/>)
/// or <see cref="ThrowNext"/> (an escaping exception, the CR-01 path) is armed for the next call. Both are one-shot.
/// </summary>
internal sealed class FakeApplier : IWallpaperApplier
{
    public int Calls { get; private set; }
    public string? LastPath { get; private set; }
    public List<string> Paths { get; } = [];
    public bool FailNext { get; set; }

    /// <summary>One-shot: the next <see cref="Apply"/> still counts the call and records the path, then throws this and clears it.</summary>
    public Exception? ThrowNext { get; set; }

    /// <summary>
    /// When non-null, every successful <see cref="Apply"/> reports this as <see cref="ApplyResult.ReadBackPath"/> instead
    /// of the applied path — the shell "showing something else" (Windows Spotlight / settings sync) that the Settings
    /// window surfaces as <c>LastErrorKind.ReadBack</c>. Sticky until <see cref="Reset"/> or set back to null.
    /// </summary>
    public string? ReadBackOverride { get; set; }

    /// <summary>Back to a clean applier: counters, paths and any still-armed one-shot failure (IN-10).</summary>
    public void Reset()
    {
        Calls = 0;
        LastPath = null;
        Paths.Clear();
        FailNext = false;
        ThrowNext = null;
        ReadBackOverride = null;
    }

    public ApplyResult Apply(string absolutePath)
    {
        Calls++;
        LastPath = absolutePath;
        Paths.Add(absolutePath);
        if (ThrowNext is { } pending)
        {
            ThrowNext = null;
            throw pending;
        }

        if (FailNext)
        {
            FailNext = false;
            return new ApplyResult(false, "fake", null, null, "forced failure");
        }

        return new ApplyResult(true, "fake", ReadBackOverride ?? absolutePath, "DWPOS_FILL", null);
    }
}
