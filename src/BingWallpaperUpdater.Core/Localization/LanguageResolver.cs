namespace BingWallpaperUpdater.Core.Localization;

/// <summary>
/// The pure UI-language rule (L10N-02, L10N-03): an explicit <c>en</c> / <c>vi</c> setting wins; <c>auto</c> (or
/// anything unrecognised) follows the OS display language when that is one of <see cref="Supported"/>, else English.
/// No I/O, no logging, no <c>CultureInfo</c> — the App layer (<c>UiCulture</c>) reads the OS language, calls this,
/// and applies the result. Kept in Core so the rule runs under xUnit on any OS.
/// </summary>
public static class LanguageResolver
{
    /// <summary>The languages that ship a resource set: the neutral English <c>Strings.resx</c> and the <c>vi</c> satellite.</summary>
    public static readonly IReadOnlyList<string> Supported = ["en", "vi"];

    /// <summary>
    /// Returns <c>en</c> or <c>vi</c>. An explicit <paramref name="setting"/> of <c>"en"</c> or <c>"vi"</c> (ordinal)
    /// wins; otherwise (<c>"auto"</c>, null, empty, unknown) the OS two-letter UI language is used, lower-cased, when
    /// it is supported ignoring case; otherwise <c>"en"</c>.
    /// </summary>
    public static string Resolve(string? setting, string? osTwoLetterUiLanguage)
    {
        if (setting is "en" or "vi")
        {
            return setting;
        }

        if (!string.IsNullOrEmpty(osTwoLetterUiLanguage))
        {
            string? match = Supported.FirstOrDefault(s => string.Equals(s, osTwoLetterUiLanguage, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return "en";
    }
}
