namespace BingWallpaperUpdater.Core.Model;

/// <summary>
/// One image as described by a catalog source. Only <see cref="Id"/> is guaranteed;
/// the niumoo markdown carries only date + ID, so title/copyright are nullable and
/// arrive from HPImageArchive enrichment (Plan 02) or stay absent.
/// </summary>
/// <param name="Id">Bing image ID (primary key).</param>
/// <param name="Date">Catalog date <c>yyyy-MM-dd</c> as listed by niumoo (one day after Bing's start date).</param>
/// <param name="StartDate">Bing <c>startdate</c> <c>yyyyMMdd</c> when known.</param>
/// <param name="Source">One of <see cref="CatalogSources"/>.</param>
public sealed record CatalogEntry(
    ImageId Id,
    string? Date,
    string? StartDate,
    string? Title,
    string? Copyright,
    string? CopyrightLink,
    string Source);

public static class CatalogSources
{
    public const string GitHub = "github";
    public const string HpImageArchive = "hpimagearchive";
}
