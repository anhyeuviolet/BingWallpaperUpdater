using System.Globalization;
using System.Runtime.Versioning;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Localization;

namespace BingWallpaperUpdater.App;

/// <summary>
/// Applies the UI language (L10N-02, L10N-03). <see cref="OsLanguage"/> is the Windows display language captured
/// once, before any override, so a later "auto" choice in the window still resolves against the real OS value.
/// <see cref="Apply"/> sets only the two UI-culture properties — <see cref="CultureInfo.DefaultThreadCurrentUICulture"/>
/// for thread-pool threads and the main thread's <see cref="Thread.CurrentUICulture"/> — and never touches
/// <c>CurrentCulture</c>: dates and numbers keep the user's Windows regional format whatever language the strings
/// are in (CLAUDE.md "What NOT to Use", RESEARCH Anti-Patterns). Must run before the first WinForms control is
/// created (Pitfall 4), which is why <c>Program.Main</c> calls it before <c>ApplicationConfiguration.Initialize()</c>.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal static class UiCulture
{
    // Static initialiser: runs on first touch of the class, which Apply forces below before any assignment.
    private static readonly string OsLanguageValue = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>The OS two-letter UI language (<c>CultureInfo.CurrentUICulture</c>, never <c>InstalledUICulture</c>) as first seen.</summary>
    public static string OsLanguage => OsLanguageValue;

    /// <summary>Resolves <paramref name="setting"/> (auto / en / vi) against <see cref="OsLanguage"/>, applies it, logs, and returns the language used.</summary>
    public static string Apply(string? setting)
    {
        string os = OsLanguage;   // read first: guarantees the capture happened before the override below
        string lang = LanguageResolver.Resolve(setting, os);
        CultureInfo culture = CultureInfo.GetCultureInfo(lang);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Log.Info($"ui language setting={setting ?? "-"} os={os} using={lang}");
        return lang;
    }
}
