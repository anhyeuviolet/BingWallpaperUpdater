using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;

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

    /// <summary>
    /// Loads the file, or returns defaults when it is missing or corrupt — and in that case writes the
    /// defaults back atomically so <c>settings.json</c> exists after the first run (T-01-08: never throws).
    /// </summary>
    public static Settings LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Settings? loaded = AtomicJsonFile.Load(path, CoreJsonContext.Default.Settings);
        if (loaded is not null)
        {
            return loaded;
        }

        var defaults = new Settings();
        AtomicJsonFile.Save(path, defaults, CoreJsonContext.Default.Settings);
        return defaults;
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        AtomicJsonFile.Save(path, this, CoreJsonContext.Default.Settings);
    }
}
