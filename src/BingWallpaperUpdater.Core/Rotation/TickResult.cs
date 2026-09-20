namespace BingWallpaperUpdater.Core.Rotation;

/// <summary>Outcome of one <c>RotationService.RunTickAsync</c> call; logged as <c>tick done ... result=</c>.</summary>
public enum TickResult
{
    /// <summary>Another tick held the single-flight gate; nothing ran (D-01).</summary>
    Busy,

    /// <summary>The tick ran and left the desktop untouched.</summary>
    NoOp,

    /// <summary>An image was applied.</summary>
    Applied,

    /// <summary>An apply was attempted and the applier reported failure.</summary>
    ApplyFailed,

    /// <summary>Neither catalog source produced a row, or the chosen download was rejected (D-13).</summary>
    FetchFailed,

    /// <summary>The token was cancelled (shutdown) or the service is disposed.</summary>
    Cancelled,

    /// <summary>An unexpected exception escaped the tick; it was logged and swallowed so the heartbeat survives.</summary>
    Failed,
}
