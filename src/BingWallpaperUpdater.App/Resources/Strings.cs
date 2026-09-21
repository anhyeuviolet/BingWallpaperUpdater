using System.Globalization;
using System.Resources;

// The neutral Strings.resx is English: an "en" lookup never probes for a satellite (L10N-01).
[assembly: NeutralResourcesLanguage("en")]

namespace BingWallpaperUpdater.App.Resources;

/// <summary>
/// Fifteen-line wrapper over the resx pair (<c>Strings.resx</c> = English, <c>Strings.vi.resx</c> = Vietnamese);
/// no designer file, no build-time generator. Lookups follow <see cref="CultureInfo.CurrentUICulture"/>, which
/// <c>UiCulture.Apply</c> sets before the first control exists and again on a runtime override (L10N-02, L10N-03).
/// A missing key renders the key text itself — visible in the UI, never an empty label, never an exception —
/// the same "null -> visible fallback" rule as <c>TrayApplicationContext.LoadTrayIcon</c>.
/// </summary>
internal static class Strings
{
    // Base name = RootNamespace (BingWallpaperUpdater.App) + folder (Resources) + file (Strings).
    private static readonly ResourceManager Rm = new("BingWallpaperUpdater.App.Resources.Strings", typeof(Strings).Assembly);

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    }
}
