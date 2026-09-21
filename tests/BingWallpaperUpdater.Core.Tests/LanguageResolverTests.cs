using BingWallpaperUpdater.Core.Localization;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// L10N-02 / L10N-03: explicit <c>en</c> / <c>vi</c> win, <c>auto</c> follows a supported OS language (case-insensitive),
/// everything else is English. Pure: no LogSink, no temp dir.
/// </summary>
public sealed class LanguageResolverTests
{
    [Theory]
    [InlineData("auto", "vi", "vi")]
    [InlineData("auto", "en", "en")]
    [InlineData("auto", "ja", "en")]
    [InlineData("auto", "VI", "vi")]
    [InlineData("vi", "en", "vi")]
    [InlineData("en", "vi", "en")]
    [InlineData(null, "vi", "vi")]
    [InlineData("xx", "vi", "vi")]
    [InlineData(null, null, "en")]
    [InlineData("", "", "en")]
    [InlineData("EN", "vi", "vi")]   // explicit values are ordinal: "EN" is not an explicit choice, so the OS wins
    [InlineData("auto", "vi-VN", "en")]   // a full culture name is not a two-letter code; the caller passes TwoLetterISOLanguageName
    public void Resolve_SettingAndOsLanguage_PicksSupportedLanguage(string? setting, string? os, string expected)
    {
        Assert.Equal(expected, LanguageResolver.Resolve(setting, os));
    }

    [Fact]
    public void Supported_IsEnThenVi()
    {
        Assert.Equal(["en", "vi"], LanguageResolver.Supported);
    }
}
