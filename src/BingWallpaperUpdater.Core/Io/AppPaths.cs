namespace BingWallpaperUpdater.Core.Io;

/// <summary>
/// Every on-disk location the app uses. All of it lives under the per-user
/// <c>%LocalAppData%\BingWallpaperUpdater</c> folder so the uninstaller can remove it wholesale.
/// </summary>
public static class AppPaths
{
    public const string FolderName = "BingWallpaperUpdater";

    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    public static string CacheDir { get; } = Path.Combine(Root, "cache");
    public static string IndexPath { get; } = Path.Combine(CacheDir, "index.json");
    public static string CatalogBodyPath { get; } = Path.Combine(CacheDir, "catalog.md");
    public static string LogPath { get; } = Path.Combine(Root, "log.txt");
    public static string SettingsPath { get; } = Path.Combine(Root, "settings.json");
    public static string StatePath { get; } = Path.Combine(Root, "state.json");

    /// <summary>Creates <see cref="Root"/> and <see cref="CacheDir"/> if they do not exist.</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
    }
}
