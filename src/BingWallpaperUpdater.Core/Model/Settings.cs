using System.Text.Json.Serialization;
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
    public const int DefaultIntervalMinutes = 30;
    public const string DefaultMode = "newest";
    public const string RandomMode = "random";
    public const string AutoLanguage = "auto";
    public const string SameMonitorMode = "same";
    public const string PerMonitorMode = "perMonitor";

    /// <summary>The resolutions the pipeline knows how to request and validate (see <c>BingImageUrl.MinDimensions</c>).</summary>
    public static readonly IReadOnlyList<string> KnownResolutions = ["UHD", "1920x1200", "1920x1080"];

    /// <summary>
    /// UI language choices (L10N-02, L10N-03): <c>auto</c> follows the Windows display language (en or vi, else en),
    /// <c>en</c> / <c>vi</c> force one. Resolved by <c>LanguageResolver</c>; never used to derive <see cref="Market"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownLanguages = ["auto", "en", "vi"];

    /// <summary>Monitor modes (WALL-03): <c>same</c> puts one image on every monitor, <c>perMonitor</c> a different one on each.</summary>
    public static readonly IReadOnlyList<string> KnownMonitorModes = [SameMonitorMode, PerMonitorMode];

    /// <summary>
    /// Bing markets offered in the UI (SRC-04); the <c>mkt</c> value is what the API receives, the image-ID market may
    /// differ (<c>ROW</c>). A hand-edited <c>xx-YY</c> outside this list is still accepted by <see cref="Sanitize"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownMarkets = ["en-US", "en-GB", "en-AU", "en-CA", "en-IN", "de-DE", "fr-FR", "ja-JP", "zh-CN", "vi-VN"];

    /// <summary>
    /// The rotation intervals the scheduler accepts, in minutes (ROT-01): 30 min, 1 h, 2 h, 4 h, 8 h, daily. Anything
    /// else in the hand-edited file falls back to <see cref="DefaultIntervalMinutes"/>; the 30 min floor is also what
    /// keeps a hand-edited <c>intervalMinutes: 1</c> from hammering GitHub and Bing (T-02-01).
    /// </summary>
    public static readonly IReadOnlyList<int> AllowedIntervals = [30, 60, 120, 240, 480, 1440];

    /// <summary>Rotation modes (ROT-02, ROT-03): <c>newest</c> follows the catalog, <c>random</c> rotates the cache.</summary>
    public static readonly IReadOnlyList<string> KnownModes = [DefaultMode, RandomMode];

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Bing market; the catalog IDs carry <c>EN-US</c> so this defaults to <c>en-US</c>.</summary>
    public string Market { get; set; } = DefaultMarket;

    /// <summary>"UHD" (original <c>_UHD.jpg</c>), "1920x1200" or "1920x1080".</summary>
    public string Resolution { get; set; } = DefaultResolution;

    /// <summary>Rotation interval in minutes, one of <see cref="AllowedIntervals"/> (D-08).</summary>
    public int IntervalMinutes { get; set; } = DefaultIntervalMinutes;

    /// <summary><c>newest</c> (default) or <c>random</c>, canonical lower case (D-08).</summary>
    public string Mode { get; set; } = DefaultMode;

    /// <summary>
    /// UI language: <c>auto</c> (default), <c>en</c> or <c>vi</c>, canonical lower case. Applied once at startup and
    /// again when changed in the window; a Phase 1/2 file without the field loads as <c>auto</c>.
    /// </summary>
    public string Language { get; set; } = AutoLanguage;

    /// <summary><c>same</c> (default) or <c>perMonitor</c>, canonical case (WALL-03). Only the mode is persisted, never monitor paths.</summary>
    public string MonitorMode { get; set; } = SameMonitorMode;

    /// <summary>
    /// The desired autostart state (INST-02: on by default). The HKCU Run value is the observed state; the settings
    /// window and the per-start rewrite reconcile the two (Plan 03-03). A Phase 1/2 file without the field loads as true.
    /// </summary>
    public bool Autostart { get; set; } = true;

    /// <summary>The interval as a <see cref="TimeSpan"/>; computed, never serialised.</summary>
    [JsonIgnore]
    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    /// <summary>True when <see cref="Mode"/> is <see cref="RandomMode"/>; computed, never serialised.</summary>
    [JsonIgnore]
    public bool IsRandomMode => string.Equals(Mode, RandomMode, StringComparison.Ordinal);

    /// <summary>True when <see cref="MonitorMode"/> is <see cref="PerMonitorMode"/>; computed, never serialised.</summary>
    [JsonIgnore]
    public bool IsPerMonitor => string.Equals(MonitorMode, PerMonitorMode, StringComparison.Ordinal);

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

        if (!AllowedIntervals.Contains(IntervalMinutes))
        {
            Log.Warn($"settings invalid field=IntervalMinutes value={IntervalMinutes} using={DefaultIntervalMinutes}");
            IntervalMinutes = DefaultIntervalMinutes;
            changed = true;
        }

        string? mode = Mode?.Trim();
        string? knownMode = mode is null
            ? null
            : KnownModes.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
        if (knownMode is null)
        {
            Log.Warn($"settings invalid field=Mode value={Describe(Mode)} using={DefaultMode}");
            Mode = DefaultMode;
            changed = true;
        }
        else if (!string.Equals(knownMode, Mode, StringComparison.Ordinal))
        {
            Mode = knownMode;
            changed = true;
        }

        string? language = Language?.Trim();
        string? knownLanguage = language is null
            ? null
            : KnownLanguages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase));
        if (knownLanguage is null)
        {
            Log.Warn($"settings invalid field=Language value={Describe(Language)} using={AutoLanguage}");
            Language = AutoLanguage;
            changed = true;
        }
        else if (!string.Equals(knownLanguage, Language, StringComparison.Ordinal))
        {
            Language = knownLanguage;
            changed = true;
        }

        string? monitorMode = MonitorMode?.Trim();
        string? knownMonitorMode = monitorMode is null
            ? null
            : KnownMonitorModes.FirstOrDefault(m => string.Equals(m, monitorMode, StringComparison.OrdinalIgnoreCase));
        if (knownMonitorMode is null)
        {
            Log.Warn($"settings invalid field=MonitorMode value={Describe(MonitorMode)} using={SameMonitorMode}");
            MonitorMode = SameMonitorMode;
            changed = true;
        }
        else if (!string.Equals(knownMonitorMode, MonitorMode, StringComparison.Ordinal))
        {
            MonitorMode = knownMonitorMode;
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
