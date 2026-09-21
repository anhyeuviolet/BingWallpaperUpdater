using System.Text.Json;
using BingWallpaperUpdater.Core.Json;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Catalog;

/// <summary>
/// Bing HPImageArchive JSON -> catalog entries, newest first. Tolerant by design: the literal body
/// <c>null</c> (what Bing returns for <c>n=0</c>), an empty body, <c>{}</c>, an empty <c>images</c> array and
/// malformed JSON all yield an empty list — never an exception (SRC-03 empty edge). Only the ID from
/// <c>urlbase</c> is trusted; <c>url</c> and <c>copyrightlink</c> are carried as text and never fetched (T-01-01).
/// </summary>
public static class HpImageArchiveParser
{
    public static IReadOnlyList<CatalogEntry> Parse(string? json)
    {
        var entries = new List<CatalogEntry>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return entries;
        }

        HpImageArchive? archive;
        try
        {
            archive = JsonSerializer.Deserialize(json, CoreJsonContext.Default.HpImageArchive);
        }
        catch (JsonException)
        {
            return entries;
        }

        if (archive?.Images is not { Count: > 0 } images)
        {
            return entries;
        }

        // De-duplicate by ID, first occurrence wins, source order preserved (SRC-05 ordering edge).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (HpImage image in images)
        {
            if (image is null || !ImageId.TryParse(image.ImageId, out ImageId id) || !seen.Add(id.Value))
            {
                continue;
            }

            entries.Add(new CatalogEntry(
                Id: id,
                Date: IsoDate(image.EndDate),   // the README date is Bing's enddate (CLAUDE.md): "20260920" -> "2026-09-20" (IN-11)
                StartDate: NullIfBlank(image.StartDate),
                Title: NullIfBlank(image.Title),
                Copyright: NullIfBlank(image.Copyright),
                CopyrightLink: NullIfBlank(image.CopyrightLink),
                Source: CatalogSources.HpImageArchive));
        }

        return entries;
    }

    /// <summary>Missing, empty or whitespace-only metadata is stored as null, never as "" (SRC-05 empty edge).</summary>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// <c>yyyyMMdd</c> -> <c>yyyy-MM-dd</c>, the README's date shape, so archive-sourced entries order and name their
    /// files exactly like README rows; anything else (missing, malformed) stays null. Shape check only — no parsing.
    /// </summary>
    private static string? IsoDate(string? compact) =>
        compact is { Length: 8 } && compact.All(char.IsAsciiDigit)
            ? $"{compact[..4]}-{compact[4..6]}-{compact[6..8]}"
            : null;
}
