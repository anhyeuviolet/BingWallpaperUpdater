namespace BingWallpaperUpdater.Core.Rotation;

/// <summary>
/// What the last tick left behind for the "last-error line" of the Settings window (locked ROADMAP note): a silent
/// offline failure, a failed apply or a read-back mismatch (Windows Spotlight / settings sync changed the wallpaper)
/// becomes visible there instead of being fought programmatically. <see cref="None"/> after a clean tick.
/// </summary>
public enum LastErrorKind { None, Fetch, Apply, ReadBack }

/// <summary>
/// Read-only view of the rotation state for the Settings window (UI-04): the current image's metadata (null when
/// the cache entry has none — the window shows blanks, never a placeholder), the static Last checked / Next check
/// times, whether a tick holds the gate (the Next button is disabled while it does, D-05) and the last-error kind.
/// Produced by <see cref="RotationService.Snapshot"/> under the service lock; never written back.
/// </summary>
public sealed record RotationSnapshot(string? Title, string? Copyright, string? Date, DateTimeOffset? LastCheckUtc, DateTimeOffset? NextDueUtc, bool TickRunning, LastErrorKind LastError);
