using System.Text.Json.Serialization;

namespace BingWallpaperUpdater.Core.Catalog;

/// <summary>
/// Wire shape of <c>https://www.bing.com/HPImageArchive.aspx?format=js&amp;idx=N&amp;n=M&amp;mkt=en-US</c>
/// (RESEARCH Spike 2). Only the fields the app reads are declared; unknown members are ignored by
/// System.Text.Json. Dates stay strings — nothing here is ever parsed numerically (SRC-03 precision edge).
/// </summary>
public sealed class HpImageArchive
{
    [JsonPropertyName("images")]
    public List<HpImage>? Images { get; set; }
}

public sealed class HpImage
{
    private const string UrlBasePrefix = "/th?id=";

    [JsonPropertyName("startdate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("fullstartdate")]
    public string? FullStartDate { get; set; }

    [JsonPropertyName("enddate")]
    public string? EndDate { get; set; }

    /// <summary>The 1920x1080 URL Bing advertises; never fetched (Pitfall 6) — the ID is what matters.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary><c>/th?id=OHR.Name_EN-US1234567890</c>; the image ID is the part after <c>/th?id=</c>.</summary>
    [JsonPropertyName("urlbase")]
    public string? UrlBase { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("copyrightlink")]
    public string? CopyrightLink { get; set; }

    [JsonPropertyName("hsh")]
    public string? Hash { get; set; }

    /// <summary>The raw ID token from <see cref="UrlBase"/>, or null when it does not start with <c>/th?id=</c>.</summary>
    [JsonIgnore]
    public string? ImageId =>
        UrlBase is { } u && u.StartsWith(UrlBasePrefix, StringComparison.Ordinal)
            ? u[UrlBasePrefix.Length..]
            : null;
}
