using System.Text.RegularExpressions;

namespace BingWallpaperUpdater.Core.Model;

/// <summary>
/// The Bing image identifier, e.g. <c>OHR.AlphornBavaria_EN-US6200857270</c>.
/// This is the primary key across catalog, cache and state; dates are display attributes only
/// (the niumoo catalog date is one day later than Bing's <c>startdate</c> for the same image).
/// </summary>
public readonly partial record struct ImageId(string Value)
{
    [GeneratedRegex(@"^OHR\.(?<name>[A-Za-z0-9]+)_(?<market>[A-Z]{2}-[A-Z]{2})(?<digits>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Parses an ID of the form <c>OHR.&lt;Name&gt;_&lt;MARKET&gt;&lt;digits&gt;</c>; anything else is rejected.</summary>
    public static bool TryParse(string? text, out ImageId id)
    {
        if (text is not null && Pattern().IsMatch(text))
        {
            id = new ImageId(text);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>The segment between <c>OHR.</c> and <c>_&lt;MARKET&gt;</c>, e.g. <c>AlphornBavaria</c>.</summary>
    public string Name => Pattern().Match(Value ?? string.Empty) is { Success: true } m ? m.Groups["name"].Value : string.Empty;

    /// <summary>The market embedded in the ID, e.g. <c>EN-US</c>.</summary>
    public string Market => Pattern().Match(Value ?? string.Empty) is { Success: true } m ? m.Groups["market"].Value : string.Empty;

    public bool Equals(ImageId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value ?? string.Empty;
}
