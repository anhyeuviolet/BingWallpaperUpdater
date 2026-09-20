using System.Text.RegularExpressions;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Catalog;

/// <summary>
/// Extracts image IDs from the niumoo/bing-wallpaper README and monthly markdown.
/// The catalog is a fixed table shape, so a single regex over the "[download 4k](...)" links is the whole
/// parser (no markdown library). Only the ID is trusted; URLs are rebuilt by <see cref="BingImageUrl"/>
/// and never used verbatim (threat T-01-01).
/// </summary>
public static partial class MarkdownCatalogParser
{
    [GeneratedRegex(@"(?<date>\d{4}-\d{2}-\d{2}) \[download 4k\]\(https://(?:cn|www)\.bing\.com/th\?id=(?<id>OHR\.[A-Za-z0-9]+_[A-Z]{2}-[A-Z]{2}\d+)_UHD\.jpg[^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Row();

    [GeneratedRegex(@"th\?id=(?<id>OHR\.[A-Za-z0-9]+_[A-Z]{2}-[A-Z]{2}\d+)_UHD\.jpg[^)]*\)Today: \[(?<copyright>[^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Today();

    /// <summary>
    /// Returns every catalog row in document order, de-duplicated by ID. Empty or row-less input yields an
    /// empty list; nothing here throws. CRLF input is fine because the pattern never anchors on line ends.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Parse(string? markdown)
    {
        var entries = new List<CatalogEntry>();
        if (string.IsNullOrEmpty(markdown))
        {
            return entries;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Row().Matches(markdown))
        {
            string idText = m.Groups["id"].Value;
            if (!ImageId.TryParse(idText, out ImageId id) || !seen.Add(id.Value))
            {
                continue;
            }

            entries.Add(new CatalogEntry(
                Id: id,
                Date: m.Groups["date"].Value,
                StartDate: null,
                Title: null,
                Copyright: null,
                CopyrightLink: null,
                Source: CatalogSources.GitHub));
        }

        return entries;
    }

    /// <summary>Reads the "Today:" hero line, the only place the markdown carries a copyright string.</summary>
    public static (ImageId Id, string Copyright)? ParseToday(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return null;
        }

        Match m = Today().Match(markdown);
        if (!m.Success || !ImageId.TryParse(m.Groups["id"].Value, out ImageId id))
        {
            return null;
        }

        return (id, m.Groups["copyright"].Value);
    }
}
