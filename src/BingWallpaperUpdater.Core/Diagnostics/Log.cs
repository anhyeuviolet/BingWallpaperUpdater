using System.Globalization;
using System.Text;

namespace BingWallpaperUpdater.Core.Diagnostics;

/// <summary>
/// Minimal append-only text log at <c>%LocalAppData%\BingWallpaperUpdater\log.txt</c>.
/// One line per event: <c>{UTC ISO-8601} {INFO|WARN} {message}</c>. Rolls to <c>log.1.txt</c> once the
/// file exceeds 256 KB. Never throws — the log is diagnostics, not control flow.
/// The fixed tokens (<c>http</c>, <c>catalog</c>, <c>cache hit</c>, <c>cache add</c>, <c>apply ok</c>, ...)
/// are parsed by <c>tools/smoke-run.ps1</c>; keep them stable.
/// Hosts, statuses, byte counts and local paths only — never user identifiers.
/// </summary>
public static class Log
{
    private const long RollBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string path)
    {
        lock (Gate)
        {
            _path = path;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? ex = null) =>
        Write("WARN", ex is null ? message : $"{message} exception={ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} {level} {message}");
        lock (Gate)
        {
            if (_path is null)
            {
                return;
            }

            try
            {
                RollIfNeeded(_path);
                using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                fs.Write(bytes, 0, bytes.Length);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RollIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > RollBytes)
        {
            string rolled = Path.Combine(info.DirectoryName ?? string.Empty, "log.1.txt");
            File.Move(path, rolled, overwrite: true);
        }
    }
}
