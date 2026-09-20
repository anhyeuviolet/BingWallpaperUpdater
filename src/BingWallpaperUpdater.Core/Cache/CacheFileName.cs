using System.Globalization;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Cache;

/// <summary>
/// Cache file naming (CACHE-03): <c>YYYY-MM-DD_&lt;Name&gt;_&lt;MARKET&gt;&lt;digits&gt;.jpg</c> for UHD,
/// <c>YYYY-MM-DD_&lt;Name&gt;_&lt;MARKET&gt;&lt;digits&gt;.{w}x{h}.jpg</c> otherwise, with a total date fallback chain
/// (catalog date, Bing start date, UTC download date). The stem carries the whole <see cref="ImageId"/> — name,
/// market and numeric suffix — so two distinct IDs can never map to one file (an ID re-issued with a new suffix,
/// or the same picture under another market after the user edits <c>Settings.Market</c>) and eviction of one
/// entry can never delete the bytes another entry, possibly the applied one, still points at. Entries written
/// by earlier builds under the shorter <c>YYYY-MM-DD_&lt;Name&gt;.jpg</c> form stay valid: the index, not the
/// naming rule, is the truth for which file an entry owns.
/// <c>&lt;Name&gt;</c> is <see cref="ImageId.Name"/> (<c>[A-Za-z0-9]+</c> by construction), <c>&lt;MARKET&gt;</c> is
/// <c>[A-Z]{2}-[A-Z]{2}</c>, <c>&lt;digits&gt;</c> is <c>[0-9]+</c>, and the date prefix is validated by shape, so no
/// remote text can put a separator into a file name (T-01-02). A final runtime check rejects anything outside
/// <c>[A-Za-z0-9_.-]</c> or containing <c>..</c> as defence in depth.
/// </summary>
public static class CacheFileName
{
    private const string Uhd = "UHD";

    public static string For(CatalogEntry entry, string resolution, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(resolution);

        string name = entry.Id.Name;
        string market = entry.Id.Market;
        string digits = entry.Id.Digits;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(market) || string.IsNullOrEmpty(digits))
        {
            throw new ArgumentException("entry has no parsable image id", nameof(entry));
        }

        string prefix = DatePrefix(entry.Date, entry.StartDate, nowUtc);
        string stem = $"{prefix}_{name}_{market}{digits}";
        string fileName;
        if (string.Equals(resolution, Uhd, StringComparison.OrdinalIgnoreCase))
        {
            fileName = $"{stem}.jpg";
        }
        else
        {
            if (!IsWidthByHeight(resolution))
            {
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "resolution must be UHD or {w}x{h}");
            }

            fileName = $"{stem}.{resolution}.jpg";
        }

        if (!IsSafeFileName(fileName))
        {
            throw new InvalidOperationException($"cache file name contains unsafe characters: {fileName}");
        }

        return fileName;
    }

    /// <summary>
    /// <paramref name="catalogDate"/> when it is <c>yyyy-MM-dd</c>, else <paramref name="startDate"/> reformatted from
    /// <c>yyyyMMdd</c>, else <paramref name="nowUtc"/> as <c>yyyy-MM-dd</c>. Shape checks only — no date parsing.
    /// </summary>
    public static string DatePrefix(string? catalogDate, string? startDate, DateTimeOffset nowUtc)
    {
        if (catalogDate is not null && IsIsoDate(catalogDate))
        {
            return catalogDate;
        }

        if (startDate is not null && IsCompactDate(startDate))
        {
            return $"{startDate[..4]}-{startDate[4..6]}-{startDate[6..8]}";
        }

        return nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static bool IsIsoDate(string s) =>
        s.Length == 10
        && AllAsciiDigits(s.AsSpan(0, 4))
        && s[4] == '-'
        && AllAsciiDigits(s.AsSpan(5, 2))
        && s[7] == '-'
        && AllAsciiDigits(s.AsSpan(8, 2));

    private static bool IsCompactDate(string s) => s.Length == 8 && AllAsciiDigits(s);

    private static bool IsWidthByHeight(string s)
    {
        int x = s.IndexOf('x');
        return x > 0
            && x < s.Length - 1
            && AllAsciiDigits(s.AsSpan(0, x))
            && AllAsciiDigits(s.AsSpan(x + 1));
    }

    private static bool AllAsciiDigits(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeFileName(string s)
    {
        if (s.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
