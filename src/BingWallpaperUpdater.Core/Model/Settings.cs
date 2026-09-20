using System.Text.RegularExpressions;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Io;
using BingWallpaperUpdater.Core.Json;

namespace BingWallpaperUpdater.Core.Model;

/// <summary>
/// User settings persisted at <c>%LocalAppData%\BingWallpaperUpdater\settings.json</c>.
/// The file is hand-editable, so every value is normalised by <see cref="Sanitize"/> after load: a value the
/// pipeline could not act on falls back to its default with a log line instead of surfacing later as an
/// exception in the download stage (WR-06). Phase 2 adds interval/mode; Phase 3 adds the remaining UI-backed fields.
/// </summary>
public sealed partial class Settings
{
    public const string DefaultMarket = "en-US";
    public const string DefaultResolution = "UHD";

    /// <summary>The resolutions the pipeline knows how to request and validate (see <c>BingImageUrl.MinDimensions</c>).</summary>
    public static readonly IReadOnlyList<string> KnownResolutions = ["UHD", "1920x1200", "1920x1080"];

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Bing market; the catalog IDs carry <c>EN-US</c> so this defaults to <c>en-US</c>.</summary>
    public string Market { get; set; } = DefaultMarket;

    /// <summary>"UHD" (original <c>_UHD.jpg</c>), "1920x1200" or "1920x1080".</summary>
    public string Resolution { get; set; } = DefaultResolution;

    // Two ASCII letters, a hyphen, two ASCII letters — the only shape the Bing mkt parameter takes.
    [GeneratedRegex("^[A-Za-z]{2}-[A-Za-z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex MarketPattern();

    /// <summary>
    /// Loads the file, or returns defaults when it is missing or corrupt — and in that case writes the
    /// defaults back atomically so <c>settings.json</c> exists after the first run (T-01-08: never throws).
    /// A loaded file is always passed through <see cref="Sanitize"/>; the file itself is left as the user wrote it.
    /// </summary>
    public static Settings LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Settings? loaded = AtomicJsonFile.Load(path, CoreJsonContext.Default.Settings);
        if (loaded is not null)
        {
            loaded.Sanitize();
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

    /// <summary>
    /// Normalises every user-editable value in place and returns true when something had to change.
    /// <see cref="Market"/> must be <c>xx-YY</c> (case is canonicalised: <c>EN-us</c> becomes <c>en-US</c>); anything
    /// else — <c>null</c>, a query-string injection such as <c>en-US&amp;idx=7</c>, control characters — becomes
    /// <see cref="DefaultMarket"/>. <see cref="Resolution"/> is matched against <see cref="KnownResolutions"/> ignoring
    /// case (<c>uhd</c> becomes <c>UHD</c>); an unknown value such as <c>4K</c> becomes <see cref="DefaultResolution"/>.
    /// Each replacement is logged once so the user can see why the file's value was not honoured.
    /// </summary>
    public bool Sanitize()
    {
        bool changed = false;

        string? market = Market?.Trim();
        if (market is not null && MarketPattern().IsMatch(market))
        {
            string canonical = market[..2].ToLowerInvariant() + "-" + market[3..].ToUpperInvariant();
            if (!string.Equals(canonical, Market, StringComparison.Ordinal))
            {
                Market = canonical;
                changed = true;
            }
        }
        else
        {
            Log.Warn($"settings invalid field=Market value={Describe(Market)} using={DefaultMarket}");
            Market = DefaultMarket;
            changed = true;
        }

        string? resolution = Resolution?.Trim();
        string? known = resolution is null
            ? null
            : KnownResolutions.FirstOrDefault(r => string.Equals(r, resolution, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            Log.Warn($"settings invalid field=Resolution value={Describe(Resolution)} using={DefaultResolution}");
            Resolution = DefaultResolution;
            changed = true;
        }
        else if (!string.Equals(known, Resolution, StringComparison.Ordinal))
        {
            Resolution = known;
            changed = true;
        }

        return changed;
    }

    /// <summary>A log-safe rendering: null shown as such, control characters escaped, length capped.</summary>
    private static string Describe(string? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        const int max = 40;
        string shown = value.Length > max ? value[..max] + "..." : value;
        var sb = new System.Text.StringBuilder(shown.Length + 2).Append('\'');
        foreach (char c in shown)
        {
            sb.Append(char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString());
        }

        return sb.Append('\'').ToString();
    }
}
