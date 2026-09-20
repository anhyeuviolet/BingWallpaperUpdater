using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BingWallpaperUpdater.Core.Diagnostics;

namespace BingWallpaperUpdater.Core.Io;

/// <summary>
/// JSON persistence with torn-write protection: serialize to <c>&lt;path&gt;.tmp</c>, then
/// <see cref="File.Move(string, string, bool)"/> over the destination (same-volume rename).
/// Loading never throws — a missing or corrupt file yields <c>null</c> so callers fall back to defaults.
/// </summary>
public static class AtomicJsonFile
{
    public static void Save<T>(string path, T value, JsonTypeInfo<T> info)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, value, info);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static T? Load<T>(string path, JsonTypeInfo<T> info) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize(fs, info);
        }
        catch (JsonException ex)
        {
            Log.Warn($"json load failed path={path}", ex);
            return null;
        }
        catch (IOException ex)
        {
            Log.Warn($"json load failed path={path}", ex);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn($"json load failed path={path}", ex);
            return null;
        }
    }
}
