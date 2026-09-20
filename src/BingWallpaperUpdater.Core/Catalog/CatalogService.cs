using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Catalog;

/// <summary>
/// Tracer form of the catalog chain: the niumoo README (conditional GET with the persisted ETag), parsed
/// into rows, newest first. Plan 02 adds HPImageArchive enrichment and fallback behind this same signature.
/// </summary>
public sealed class CatalogService
{
    private readonly HttpGateway _http;
    private readonly AppState _state;
    private readonly string _catalogBodyPath;
    private readonly Uri _readmeUrl;

    public CatalogService(HttpGateway http, AppState state, string catalogBodyPath, Uri? readmeUrlOverride = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(catalogBodyPath);
        _http = http;
        _state = state;
        _catalogBodyPath = catalogBodyPath;
        _readmeUrl = readmeUrlOverride ?? BingImageUrl.CatalogReadme();
    }

    /// <summary>True once a 429 from raw.githubusercontent.com switched this process to HPImageArchive only.</summary>
    public bool GitHubDisabledForSession => throw new NotImplementedException();

    /// <summary>Full enriched list, newest first. Implemented in the GREEN step of Plan 02 Task 1.</summary>
    public Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(string market, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Monthly archive rows (SRC-02), no enrichment. Implemented in the GREEN step of Plan 02 Task 1.</summary>
    public Task<IReadOnlyList<CatalogEntry>> GetMonthAsync(int year, int month, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Pages HPImageArchive idx 0..7 with n=8 until <paramref name="count"/> entries. Implemented in the GREEN step.</summary>
    public Task<IReadOnlyList<CatalogEntry>> GetArchivePagesAsync(string market, int count, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>The newest catalog entry, or null when nothing could be determined (already logged).</summary>
    public async Task<CatalogEntry?> GetNewestAsync(string market, CancellationToken ct)
    {
        try
        {
            string? body = await FetchBodyAsync(ct).ConfigureAwait(false);
            if (body is null)
            {
                return null;
            }

            IReadOnlyList<CatalogEntry> rows = MarkdownCatalogParser.Parse(body);
            if (rows.Count == 0)
            {
                Log.Warn("pipeline failed stage=catalog error=no rows parsed");
                return null;
            }

            CatalogEntry newest = rows[0];
            Log.Info($"catalog source={CatalogSources.GitHub} rows={rows.Count} newest={newest.Id}");

            if (MarkdownCatalogParser.ParseToday(body) is { } today && today.Id.Equals(newest.Id))
            {
                newest = newest with { Copyright = today.Copyright };
            }

            return newest;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            Log.Warn($"pipeline failed stage=catalog error={ex.Message}");
            return null;
        }
    }

    private async Task<string?> FetchBodyAsync(CancellationToken ct)
    {
        TextResponse response = await _http.GetTextAsync(_readmeUrl, _state.CatalogEtag, ct).ConfigureAwait(false);

        if (response.Status == 200 && response.Body is not null)
        {
            WriteBodyAtomically(_catalogBodyPath, response.Body);
            _state.CatalogEtag = response.ETag;
            _state.CatalogFetchedUtc = DateTimeOffset.UtcNow;
            return response.Body;
        }

        if (response.Status == 304 && File.Exists(_catalogBodyPath))
        {
            _state.CatalogFetchedUtc = DateTimeOffset.UtcNow;
            return await File.ReadAllTextAsync(_catalogBodyPath, ct).ConfigureAwait(false);
        }

        Log.Warn($"pipeline failed stage=catalog error=status {response.Status}");
        return null;
    }

    private static void WriteBodyAtomically(string path, string body)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, body);
        File.Move(tmp, path, overwrite: true);
    }
}
