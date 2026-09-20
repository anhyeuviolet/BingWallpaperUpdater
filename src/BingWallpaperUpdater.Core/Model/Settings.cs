namespace BingWallpaperUpdater.Core.Model;

/// <summary>
/// User settings persisted at <c>%LocalAppData%\BingWallpaperUpdater\settings.json</c>.
/// Phase 2 adds interval/mode; Phase 3 adds the remaining UI-backed fields.
/// </summary>
public sealed class Settings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Bing market; the catalog IDs carry <c>EN-US</c> so this defaults to <c>en-US</c>.</summary>
    public string Market { get; set; } = "en-US";

    /// <summary>"UHD" (original <c>_UHD.jpg</c>), "1920x1200" or "1920x1080".</summary>
    public string Resolution { get; set; } = "UHD";
}
