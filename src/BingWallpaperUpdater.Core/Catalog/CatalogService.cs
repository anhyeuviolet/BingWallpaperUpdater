using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Catalog;

/// <summary>
/// The catalog chain (RESEARCH Pattern 3). For <c>en-US</c>: the niumoo README via conditional GET
/// (ETag persisted in <see cref="AppState"/>, body persisted at <c>cache\catalog.md</c>), parsed into rows;
/// when that yields nothing — transport failure, non-200, 429, row-less body — HPImageArchive becomes the
/// list. HPImageArchive (<c>idx=0&amp;n=8</c>) is fetched on every refresh regardless, to enrich README rows with
/// title / copyright / start date by exact image ID; metadata never blocks a wallpaper change. For any other
/// market HPImageArchive is the only source. Retries, Retry-After and www→cn failover live in
/// <see cref="HttpGateway"/>; this class only decides which source wins.
/// </summary>
public sealed class CatalogService
{
    /// <summary>
    /// Debug/verification override for the README URL (RESEARCH Open Question 3). Must be an absolute https URL
    /// on an allow-listed host; anything else is logged and treated as a GitHub failure for that call (T-01-13).
    /// </summary>
    public const string CatalogUrlEnvironmentVariable = "BWU_CATALOG_URL";

    private const string EnUsMarket = "en-US";
    private const int ArchivePageSize = 8;
    private const int ArchiveMaxIndex = 7;

    private readonly HttpGateway _http;
    private readonly AppState _state;
    private readonly string _catalogBodyPath;
    private readonly Uri? _readmeUrlOverride;

    public CatalogService(HttpGateway http, AppState state, string catalogBodyPath, Uri? readmeUrlOverride = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(catalogBodyPath);
        _http = http;
        _state = state;
        _catalogBodyPath = catalogBodyPath;
        _readmeUrlOverride = readmeUrlOverride;
    }

    /// <summary>
    /// True once raw.githubusercontent.com answered 429 in this process: GitHub is not asked again until restart
    /// and HPImageArchive serves the session (PITFALLS P6, T-01-14).
    /// </summary>
    public bool GitHubDisabledForSession { get; private set; }

    /// <summary>
    /// The full list, newest first: README rows enriched from HPImageArchive for <c>en-US</c>, otherwise
    /// HPImageArchive alone. Empty only when both sources are empty — the caller (the rotation tick) owns the
    /// <c>pipeline failed stage=catalog</c> warning for that case. Never throws for network or parse reasons.
    /// </summary>
    public async Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(string market, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(market);

        IReadOnlyList<CatalogEntry> github = Array.Empty<CatalogEntry>();
        (ImageId Id, string Copyright)? today = null;
        if (string.Equals(market, EnUsMarket, StringComparison.OrdinalIgnoreCase) && !GitHubDisabledForSession)
        {
            (github, today) = await FetchGitHubAsync(ct).ConfigureAwait(false);
        }

        IReadOnlyList<CatalogEntry> archive = await FetchArchiveAsync(market, ct).ConfigureAwait(false);

        if (github.Count > 0)
        {
            IReadOnlyList<CatalogEntry> merged = Enrich(github, archive, today);
            Log.Info($"catalog source={CatalogSources.GitHub} rows={merged.Count} newest={merged[0].Id}");
            return merged;
        }

        if (archive.Count > 0)
        {
            Log.Info($"catalog source={CatalogSources.HpImageArchive} rows={archive.Count} newest={archive[0].Id}");
            return archive;
        }

        return Array.Empty<CatalogEntry>();
    }

    /// <summary>Rows of one monthly archive page (SRC-02). No conditional GET, no enrichment; empty on any failure.</summary>
    public async Task<IReadOnlyList<CatalogEntry>> GetMonthAsync(int year, int month, CancellationToken ct)
    {
        if (GitHubDisabledForSession)
        {
            Log.Warn("catalog month skipped reason=github disabled-for-session");
            return Array.Empty<CatalogEntry>();
        }

        Uri url = BingImageUrl.CatalogMonth(year, month);
        try
        {
            TextResponse response = await _http.GetTextAsync(url, null, ct).ConfigureAwait(false);
            if (response.Status == 429)
            {
                DisableGitHub(response.Status);
                return Array.Empty<CatalogEntry>();
            }

            if (response.Status != 200 || response.Body is null)
            {
                Log.Warn($"catalog month failed reason=status {response.Status}");
                return Array.Empty<CatalogEntry>();
            }

            return MarkdownCatalogParser.Parse(response.Body);
        }
        catch (Exception ex) when (IsSourceFailure(ex, ct))
        {
            Log.Warn($"catalog month failed reason={ex.GetType().Name}: {ex.Message}");
            return Array.Empty<CatalogEntry>();
        }
    }

    /// <summary>
    /// Deeper HPImageArchive history: pages <c>idx</c> 0..7 with <c>n=8</c> (Bing's caps — SRC-03 boundary edge)
    /// until <paramref name="count"/> unique entries are collected, a page adds nothing new, or the last page is
    /// reached. Never emits <c>n</c> above 8 or <c>idx</c> above 7.
    /// </summary>
    public async Task<IReadOnlyList<CatalogEntry>> GetArchivePagesAsync(string market, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(market);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var collected = new List<CatalogEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int idx = 0; idx <= ArchiveMaxIndex && collected.Count < count; idx++)
        {
            IReadOnlyList<CatalogEntry> page = await FetchArchivePageAsync(market, idx, ct).ConfigureAwait(false);
            int added = 0;
            foreach (CatalogEntry entry in page)
            {
                if (seen.Add(entry.Id.Value))
                {
                    collected.Add(entry);
                    added++;
                }
            }

            if (added == 0)
            {
                break;
            }
        }

        return collected;
    }

    // ---- GitHub source ------------------------------------------------------------------------------

    private async Task<(IReadOnlyList<CatalogEntry> Rows, (ImageId Id, string Copyright)? Today)> FetchGitHubAsync(CancellationToken ct)
    {
        Uri? url = ResolveReadmeUrl();
        if (url is null)
        {
            return (Array.Empty<CatalogEntry>(), null);
        }

        try
        {
            string? body = await FetchReadmeBodyAsync(url, ct).ConfigureAwait(false);
            if (body is null)
            {
                return (Array.Empty<CatalogEntry>(), null);
            }

            IReadOnlyList<CatalogEntry> rows = MarkdownCatalogParser.Parse(body);
            if (rows.Count == 0)
            {
                Log.Warn("catalog github failed reason=no rows parsed");
                return (rows, null);
            }

            return (rows, MarkdownCatalogParser.ParseToday(body));
        }
        catch (Exception ex) when (IsSourceFailure(ex, ct))
        {
            Log.Warn($"catalog github failed reason={ex.GetType().Name}: {ex.Message}");
            return (Array.Empty<CatalogEntry>(), null);
        }
    }

    /// <summary>Constructor override, else <c>BWU_CATALOG_URL</c> (validated), else the niumoo README.</summary>
    private Uri? ResolveReadmeUrl()
    {
        if (_readmeUrlOverride is not null)
        {
            return _readmeUrlOverride;
        }

        string? env = Environment.GetEnvironmentVariable(CatalogUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(env))
        {
            return BingImageUrl.CatalogReadme();
        }

        if (!Uri.TryCreate(env.Trim(), UriKind.Absolute, out Uri? candidate) || !HostAllowList.IsAllowed(candidate))
        {
            string host = candidate?.Host is { Length: > 0 } h ? h : "<invalid>";
            Log.Warn($"catalog override rejected host={host}");
            return null;
        }

        return candidate;
    }

    private async Task<string?> FetchReadmeBodyAsync(Uri url, CancellationToken ct)
    {
        // Only offer the ETag when the body it identifies is still on disk; a 304 without a body is useless.
        string? ifNoneMatch = File.Exists(_catalogBodyPath) ? _state.CatalogEtag : null;
        TextResponse response = await _http.GetTextAsync(url, ifNoneMatch, ct).ConfigureAwait(false);

        switch (response.Status)
        {
            case 200 when response.Body is not null:
                WriteBodyAtomically(_catalogBodyPath, response.Body);
                _state.CatalogEtag = response.ETag;
                _state.CatalogFetchedUtc = DateTimeOffset.UtcNow;
                return response.Body;

            case 304 when File.Exists(_catalogBodyPath):
                _state.CatalogFetchedUtc = DateTimeOffset.UtcNow;
                return await File.ReadAllTextAsync(_catalogBodyPath, ct).ConfigureAwait(false);

            case 304:
                Log.Warn("catalog github failed reason=304 without a saved catalog body");
                _state.CatalogEtag = null;
                return null;

            case 429:
                DisableGitHub(response.Status);
                return null;

            default:
                Log.Warn($"catalog github failed reason=status {response.Status}");
                return null;
        }
    }

    private void DisableGitHub(int status)
    {
        GitHubDisabledForSession = true;
        Log.Warn($"catalog github disabled-for-session status={status}");
    }

    // ---- HPImageArchive source ----------------------------------------------------------------------

    private async Task<IReadOnlyList<CatalogEntry>> FetchArchiveAsync(string market, CancellationToken ct)
    {
        try
        {
            TextResponse response = await _http.GetTextAsync(BingImageUrl.Archive(BingImageUrl.PrimaryHost, 0, 8, market), null, ct).ConfigureAwait(false);
            if (response.Status != 200)
            {
                Log.Warn($"enrich failed reason=status {response.Status}");
                return Array.Empty<CatalogEntry>();
            }

            IReadOnlyList<CatalogEntry> rows = HpImageArchiveParser.Parse(response.Body);
            if (rows.Count == 0)
            {
                Log.Warn("enrich failed reason=no images parsed");
            }

            return rows;
        }
        catch (Exception ex) when (IsSourceFailure(ex, ct))
        {
            Log.Warn($"enrich failed reason={ex.GetType().Name}: {ex.Message}");
            return Array.Empty<CatalogEntry>();
        }
    }

    private async Task<IReadOnlyList<CatalogEntry>> FetchArchivePageAsync(string market, int idx, CancellationToken ct)
    {
        try
        {
            TextResponse response = await _http.GetTextAsync(BingImageUrl.Archive(BingImageUrl.PrimaryHost, idx, ArchivePageSize, market), null, ct).ConfigureAwait(false);
            return response.Status == 200 ? HpImageArchiveParser.Parse(response.Body) : Array.Empty<CatalogEntry>();
        }
        catch (Exception ex) when (IsSourceFailure(ex, ct))
        {
            Log.Warn($"archive page failed idx={idx} reason={ex.GetType().Name}: {ex.Message}");
            return Array.Empty<CatalogEntry>();
        }
    }

    // ---- merge ----------------------------------------------------------------------------------------

    /// <summary>
    /// README rows in README order, each filled from the HPImageArchive entry with the identical ID (ordinal);
    /// only null fields are filled. The newest row additionally takes the README "Today:" copyright when still
    /// missing. HPImageArchive-only IDs are never appended (SRC-05 adjacency edge).
    /// </summary>
    private static IReadOnlyList<CatalogEntry> Enrich(IReadOnlyList<CatalogEntry> github, IReadOnlyList<CatalogEntry> archive, (ImageId Id, string Copyright)? today)
    {
        var byId = new Dictionary<string, CatalogEntry>(archive.Count, StringComparer.Ordinal);
        foreach (CatalogEntry entry in archive)
        {
            byId.TryAdd(entry.Id.Value, entry);
        }

        var result = new List<CatalogEntry>(github.Count);
        int hits = 0;
        for (int i = 0; i < github.Count; i++)
        {
            CatalogEntry row = github[i];
            if (byId.TryGetValue(row.Id.Value, out CatalogEntry? meta))
            {
                CatalogEntry filled = row with
                {
                    StartDate = row.StartDate ?? meta.StartDate,
                    Title = row.Title ?? meta.Title,
                    Copyright = row.Copyright ?? meta.Copyright,
                    CopyrightLink = row.CopyrightLink ?? meta.CopyrightLink,
                };
                if (filled != row)
                {
                    hits++;
                }

                row = filled;
            }

            if (i == 0 && row.Copyright is null && today is { } t && t.Id.Equals(row.Id))
            {
                row = row with { Copyright = t.Copyright };
            }

            result.Add(row);
        }

        Log.Info($"enrich hits={hits}");
        return result;
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>Failures that mean "this source is unavailable now" — the caller's own cancellation is not one of them.</summary>
    private static bool IsSourceFailure(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested
        && ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException;

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
