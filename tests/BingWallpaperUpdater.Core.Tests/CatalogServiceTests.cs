using System.Net.Http.Headers;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Diagnostics;
using BingWallpaperUpdater.Core.Model;
using BingWallpaperUpdater.Core.Net;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// Catalog chain over a fake handler: README (conditional GET) + HPImageArchive enrichment, automatic fallback,
/// 304 reuse, 429 session switch, the BWU_CATALOG_URL override, monthly archives and idx paging.
/// Shares the process-global <see cref="Log"/> sink with every other log-asserting class via the "LogSink" collection.
/// </summary>
[Collection("LogSink")]
public sealed class CatalogServiceTests : IDisposable
{
    private const string GitHubHost = "raw.githubusercontent.com";
    private const string BingHost = "www.bing.com";
    private const string ReadmePath = "/niumoo/bing-wallpaper/main/README.md";
    private const string ArchivePath = "/HPImageArchive.aspx";
    private const string OverrideVariable = "BWU_CATALOG_URL";

    private readonly string _dir;
    private readonly string _catalogPath;
    private readonly string _logPath;

    public CatalogServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bwu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _catalogPath = Path.Combine(_dir, "catalog.md");
        _logPath = Path.Combine(_dir, "log.txt");
        Log.Initialize(_logPath);
    }

    public void Dispose()
    {
        Log.Initialize(Path.Combine(_dir, "closed.log"));
        Environment.SetEnvironmentVariable(OverrideVariable, null);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Readme() => Fixture("README.sample.md");

    private static string Archive() => Fixture("hpimagearchive.sample.json");

    private string LogText() => File.Exists(_logPath) ? File.ReadAllText(_logPath) : string.Empty;

    /// <summary>Retries still happen (the fake sees every attempt) but nothing sleeps: these tests are about source selection, not timing.</summary>
    private static RetryPolicy NoDelay() => new(delay: (_, _) => Task.CompletedTask, random: new Random(1));

    private CatalogService Service(FakeHttpHandler fake, AppState? state = null, Uri? readmeOverride = null) =>
        new(new HttpGateway(fake, retry: NoDelay()), state ?? new AppState(), _catalogPath, readmeOverride);

    private static FakeHttpHandler Fake(Func<HttpRequestMessage, HttpResponseMessage>? github, Func<HttpRequestMessage, HttpResponseMessage>? archive)
    {
        var fake = new FakeHttpHandler();
        if (github is not null)
        {
            fake.Map(GitHubHost, "/", github);
        }

        if (archive is not null)
        {
            // The gateway fails over www -> cn on transport errors / 5xx, so the archive responder answers on both.
            fake.Map(BingHost, ArchivePath, archive);
            fake.Map(BingImageUrl.RetryHost, ArchivePath, archive);
        }

        return fake;
    }

    private static string ArchiveJson(params (string Id, string Title)[] images) =>
        "{\"images\":[" + string.Join(",", images.Select(i =>
            $"{{\"startdate\":\"20260901\",\"urlbase\":\"/th?id={i.Id}\",\"title\":\"{i.Title}\",\"copyright\":\"(c) {i.Title}\",\"copyrightlink\":\"https://www.bing.com/search?q={i.Title}\"}}"))
        + "]}";

    // ---- enrichment -----------------------------------------------------------------------------

    [Fact]
    public async Task GetCatalog_EnUs_EnrichesReadmeRowsByIdAndPreservesReadmeOrder()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, Readme()), _ => FakeHttpHandler.Json(200, Archive()));
        IReadOnlyList<CatalogEntry> readmeRows = MarkdownCatalogParser.Parse(Readme());
        var hpIds = HpImageArchiveParser.Parse(Archive()).Select(e => e.Id.Value).ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Equal(readmeRows.Count, rows.Count);
        Assert.Equal(readmeRows.Select(r => r.Id.Value), rows.Select(r => r.Id.Value));
        Assert.Contains(rows, r => hpIds.Contains(r.Id.Value));
        Assert.All(rows, r =>
        {
            Assert.Equal(CatalogSources.GitHub, r.Source);
            if (hpIds.Contains(r.Id.Value))
            {
                Assert.NotNull(r.Title);
                Assert.NotNull(r.Copyright);
                Assert.NotNull(r.CopyrightLink);
                Assert.NotNull(r.StartDate);
            }
            else
            {
                Assert.Null(r.Title);
                Assert.Null(r.Copyright);
                Assert.Null(r.CopyrightLink);
                Assert.Null(r.StartDate);
            }
        });
        Assert.Contains("catalog source=github rows=", LogText());
        Assert.Contains("enrich hits=", LogText());
    }

    [Fact]
    public async Task GetCatalog_EnUs_ArchiveOnlyIdIsNotAppended_AndSharedIdYieldsOneEntry()
    {
        IReadOnlyList<CatalogEntry> readmeRows = MarkdownCatalogParser.Parse(Readme());
        string newest = readmeRows[0].Id.Value;
        const string hpOnly = "OHR.OnlyInArchive_EN-US9999999999";
        string archive = ArchiveJson((newest, "Enriched newest"), (hpOnly, "Never listed"));
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, Readme()), _ => FakeHttpHandler.Json(200, archive));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Equal(readmeRows.Count, rows.Count);
        Assert.DoesNotContain(rows, r => r.Id.Value == hpOnly);
        Assert.Single(rows, r => r.Id.Value == newest);
        Assert.Equal("Enriched newest", rows[0].Title);
        Assert.Equal(CatalogSources.GitHub, rows[0].Source);
    }

    [Fact]
    public async Task GetCatalog_ArchiveThrows_UsesTodayCopyrightForNewestRowAndStillReturns()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, Readme()), FakeHttpHandler.Throw(new HttpRequestException("boom")));
        (ImageId Id, string Copyright)? today = MarkdownCatalogParser.ParseToday(Readme());

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Equal(MarkdownCatalogParser.Parse(Readme()).Count, rows.Count);
        Assert.NotNull(today);
        Assert.Equal(today.Value.Copyright, rows[0].Copyright);
        Assert.Null(rows[0].Title);
        Assert.Contains("enrich failed reason=", LogText());
    }

    // ---- fallback -------------------------------------------------------------------------------

    /// <summary>WR-03: an oversized README is refused by the gateway's text cap and the catalog falls back to HPImageArchive.</summary>
    [Fact]
    public async Task GetCatalog_ReadmeBodyAboveTextCap_FallsBackToArchiveAfterASingleRequest()
    {
        var huge = new byte[HttpGateway.MaxTextBytes + 1];
        Array.Fill(huge, (byte)'#');
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Bytes(200, "text/plain", huge), _ => FakeHttpHandler.Json(200, Archive()));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal(CatalogSources.HpImageArchive, r.Source));
        Assert.Single(fake.RequestsTo(GitHubHost));
        Assert.Contains("catalog github failed reason=HttpRequestException", LogText());
        Assert.False(File.Exists(_catalogPath), "an oversized body must never reach catalog.md");
    }

    [Fact]
    public async Task GetCatalog_ReadmeThrows_FallsBackToArchive()
    {
        FakeHttpHandler fake = Fake(FakeHttpHandler.Throw(new HttpRequestException("unreachable")), _ => FakeHttpHandler.Json(200, Archive()));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal(CatalogSources.HpImageArchive, r.Source));
        Assert.Contains("catalog github failed reason=", LogText());
        Assert.Contains("catalog source=hpimagearchive", LogText());
    }

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task GetCatalog_ReadmeErrorStatus_FallbackToArchive(int status)
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(status, "nope"), _ => FakeHttpHandler.Json(200, Archive()));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal(CatalogSources.HpImageArchive, r.Source));
        Assert.Contains("catalog source=hpimagearchive", LogText());
    }

    [Fact]
    public async Task GetCatalog_ReadmeWithoutRows_FallbackToArchive()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, "## Bing Wallpaper\n\nnothing to see\n"), _ => FakeHttpHandler.Json(200, Archive()));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Equal(8, rows.Count);
        Assert.Equal(CatalogSources.HpImageArchive, rows[0].Source);
        Assert.Contains("catalog source=hpimagearchive", LogText());
    }

    [Fact]
    public async Task GetCatalog_BothSourcesFail_ReturnsEmptyWithoutPipelineWarning()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(500, ""), _ => FakeHttpHandler.Json(200, "null"));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Empty(rows);
        // The "pipeline failed stage=catalog" warning belongs to the rotation tick (RotationServiceTests), not here.
        Assert.DoesNotContain("pipeline failed stage=catalog", LogText());
    }

    [Fact]
    public async Task GetCatalog_FirstRowIsNewestEnrichedReadmeRow()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, Readme()), _ => FakeHttpHandler.Json(200, Archive()));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.NotEmpty(rows);
        Assert.Equal(MarkdownCatalogParser.Parse(Readme())[0].Id, rows[0].Id);
        Assert.NotNull(rows[0].Title);
    }

    // ---- encoding -------------------------------------------------------------------------------

    [Fact]
    public async Task GetCatalog_CrlfReadme_YieldsSameIdsAsLf()
    {
        string lf = Readme().Replace("\r\n", "\n", StringComparison.Ordinal);
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        FakeHttpHandler lfFake = Fake(_ => FakeHttpHandler.Text(200, lf), _ => FakeHttpHandler.Json(200, "null"));
        FakeHttpHandler crlfFake = Fake(_ => FakeHttpHandler.Text(200, crlf), _ => FakeHttpHandler.Json(200, "null"));

        IReadOnlyList<CatalogEntry> fromLf = await Service(lfFake).GetCatalogAsync("en-US", CancellationToken.None);
        IReadOnlyList<CatalogEntry> fromCrlf = await Service(crlfFake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.NotEmpty(fromLf);
        Assert.Equal(fromLf.Select(r => r.Id.Value), fromCrlf.Select(r => r.Id.Value));
    }

    // ---- conditional GET ------------------------------------------------------------------------

    [Fact]
    public async Task GetCatalog_SecondCall_SendsIfNoneMatch_AndNotModifiedReusesSavedBody()
    {
        const string etag = "\"35907e81c414054da54b5c1d5c818697214430c8254dc10bc1ce3f18258adbbb\"";
        int githubCalls = 0;
        var fake = new FakeHttpHandler()
            .Map(GitHubHost, "/", r => ++githubCalls == 1 ? FakeHttpHandler.Text(200, Readme(), etag) : FakeHttpHandler.Status(304))
            .Map(BingHost, ArchivePath, _ => FakeHttpHandler.Json(200, "null"));
        var state = new AppState();
        CatalogService service = Service(fake, state);

        IReadOnlyList<CatalogEntry> first = await service.GetCatalogAsync("en-US", CancellationToken.None);
        Assert.Equal(etag, state.CatalogEtag);
        Assert.True(File.Exists(_catalogPath));
        Assert.Equal(Readme(), File.ReadAllText(_catalogPath));

        IReadOnlyList<CatalogEntry> second = await service.GetCatalogAsync("en-US", CancellationToken.None);

        RecordedRequest secondRequest = fake.RequestsTo(GitHubHost).ElementAt(1);
        Assert.Equal(etag, secondRequest.Header("If-None-Match"));
        Assert.Null(fake.RequestsTo(GitHubHost).First().Header("If-None-Match"));
        Assert.Equal(first.Select(r => r.Id.Value), second.Select(r => r.Id.Value));
        Assert.Contains($"http {GitHubHost} 304 0", LogText());
        Assert.NotNull(state.CatalogFetchedUtc);
    }

    [Fact]
    public async Task GetCatalog_NotModifiedWithoutSavedBody_FallbackToArchive()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Status(304), _ => FakeHttpHandler.Json(200, Archive()));
        var state = new AppState { CatalogEtag = "\"stale\"" };

        IReadOnlyList<CatalogEntry> rows = await Service(fake, state).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Equal(8, rows.Count);
        Assert.Equal(CatalogSources.HpImageArchive, rows[0].Source);
    }

    // ---- 429 session switch ----------------------------------------------------------------------

    [Fact]
    public async Task GetCatalog_Readme429_DisablesGitHubForSessionAndServesArchive()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(429, "slow down"), _ => FakeHttpHandler.Json(200, Archive()));
        CatalogService service = Service(fake);
        Assert.False(service.GitHubDisabledForSession);

        IReadOnlyList<CatalogEntry> rows = await service.GetCatalogAsync("en-US", CancellationToken.None);

        Assert.True(service.GitHubDisabledForSession);
        Assert.Equal(8, rows.Count);
        Assert.Equal(CatalogSources.HpImageArchive, rows[0].Source);
        Assert.Contains("catalog github disabled-for-session status=429", LogText());
        Assert.Single(fake.RequestsTo(GitHubHost));

        await service.GetCatalogAsync("en-US", CancellationToken.None);
        await service.GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Single(fake.RequestsTo(GitHubHost));
        Assert.Equal(3, fake.RequestsTo(BingHost).Count());
    }

    // ---- markets --------------------------------------------------------------------------------

    [Fact]
    public async Task GetCatalog_NonEnUsMarket_UsesArchiveOnlyWithThatMarket()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, Readme()), _ => FakeHttpHandler.Json(200, Archive()));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("de-DE", CancellationToken.None);

        Assert.Empty(fake.RequestsTo(GitHubHost));
        RecordedRequest archive = Assert.Single(fake.RequestsTo(BingHost));
        Assert.Equal("/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=de-DE", archive.Uri.PathAndQuery);
        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal(CatalogSources.HpImageArchive, r.Source));
    }

    // ---- monthly archive ------------------------------------------------------------------------

    [Fact]
    public async Task GetMonth_RequestsMonthlyReadmeAndReturnsRows()
    {
        var fake = new FakeHttpHandler().Map(GitHubHost, "/niumoo/bing-wallpaper/main/picture/2026-09/README.md", _ => FakeHttpHandler.Text(200, Fixture("picture-2026-09.sample.md")));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetMonthAsync(2026, 9, CancellationToken.None);

        RecordedRequest request = Assert.Single(fake.Requests);
        Assert.Equal("/niumoo/bing-wallpaper/main/picture/2026-09/README.md", request.Uri.AbsolutePath);
        Assert.True(rows.Count >= 20, $"expected >= 20 rows, got {rows.Count}");
        Assert.All(rows, r => Assert.Equal(CatalogSources.GitHub, r.Source));
    }

    [Fact]
    public async Task GetMonth_Failure_ReturnsEmptyList()
    {
        var fake = new FakeHttpHandler(FakeHttpHandler.Throw(new HttpRequestException("down")));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetMonthAsync(2026, 8, CancellationToken.None);

        Assert.Empty(rows);
    }

    // ---- BWU_CATALOG_URL override ---------------------------------------------------------------

    [Fact]
    public async Task GetCatalog_OverrideOnAllowListedHost_ReplacesReadmeUrl()
    {
        const string overridePath = "/niumoo/bing-wallpaper/main/does-not-exist/README.md";
        var fake = new FakeHttpHandler()
            .Map(GitHubHost, overridePath, _ => FakeHttpHandler.Text(200, Readme()))
            .Map(BingHost, ArchivePath, _ => FakeHttpHandler.Json(200, "null"));
        Environment.SetEnvironmentVariable(OverrideVariable, $"https://{GitHubHost}{overridePath}");

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        RecordedRequest github = Assert.Single(fake.RequestsTo(GitHubHost));
        Assert.Equal(overridePath, github.Uri.AbsolutePath);
        Assert.NotEmpty(rows);
        Assert.Equal(CatalogSources.GitHub, rows[0].Source);
    }

    [Fact]
    public async Task GetCatalog_OverrideOnForeignHost_IsRejectedBeforeAnyRequestAndFallsBack()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, Readme()), _ => FakeHttpHandler.Json(200, Archive()));
        Environment.SetEnvironmentVariable(OverrideVariable, "https://example.com/README.md");

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.DoesNotContain(fake.Requests, r => r.Uri.Host == "example.com");
        Assert.Empty(fake.RequestsTo(GitHubHost));
        Assert.Contains("catalog override rejected host=example.com", LogText());
        Assert.Equal(8, rows.Count);
        Assert.Equal(CatalogSources.HpImageArchive, rows[0].Source);
    }

    [Fact]
    public async Task GetCatalog_OverrideWithHttpScheme_IsRejected()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, Readme()), _ => FakeHttpHandler.Json(200, Archive()));
        Environment.SetEnvironmentVariable(OverrideVariable, $"http://{GitHubHost}{ReadmePath}");

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.Empty(fake.RequestsTo(GitHubHost));
        Assert.Contains("catalog override rejected host=", LogText());
        Assert.Equal(CatalogSources.HpImageArchive, rows[0].Source);
    }

    // ---- idx paging -----------------------------------------------------------------------------

    [Fact]
    public async Task GetArchivePages_SixteenEntries_PagesIdxZeroAndOneWithNEight()
    {
        string page0 = ArchiveJson(Enumerable.Range(0, 8).Select(i => ($"OHR.PageZero{i}_EN-US100000000{i}", $"p0-{i}")).ToArray());
        string page1 = ArchiveJson(Enumerable.Range(0, 8).Select(i => ($"OHR.PageOne{i}_EN-US200000000{i}", $"p1-{i}")).ToArray());
        var fake = new FakeHttpHandler().Map(BingHost, ArchivePath, r =>
        {
            string query = r.RequestUri!.Query;
            return query.Contains("idx=0", StringComparison.Ordinal) ? FakeHttpHandler.Json(200, page0)
                : query.Contains("idx=1", StringComparison.Ordinal) ? FakeHttpHandler.Json(200, page1)
                : FakeHttpHandler.Json(200, "null");
        });

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetArchivePagesAsync("en-US", 16, CancellationToken.None);

        Assert.Equal(16, rows.Count);
        Assert.Equal(16, rows.Select(r => r.Id.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=en-US", fake.Requests[0].Uri.PathAndQuery);
        Assert.Equal("/HPImageArchive.aspx?format=js&idx=1&n=8&mkt=en-US", fake.Requests[1].Uri.PathAndQuery);
        Assert.All(fake.Requests, r => Assert.Contains("&n=8&", r.Uri.Query));
    }

    [Fact]
    public async Task GetArchivePages_StopsAtIdxSevenAndOnRepeatedPages()
    {
        string same = ArchiveJson(Enumerable.Range(0, 8).Select(i => ($"OHR.Same{i}_EN-US300000000{i}", $"s-{i}")).ToArray());
        var fake = new FakeHttpHandler().Map(BingHost, ArchivePath, _ => FakeHttpHandler.Json(200, same));

        IReadOnlyList<CatalogEntry> rows = await Service(fake).GetArchivePagesAsync("en-US", 64, CancellationToken.None);

        Assert.Equal(8, rows.Count);
        Assert.True(fake.Requests.Count <= 8, $"expected at most 8 page requests (idx 0..7), got {fake.Requests.Count}");
        Assert.All(fake.Requests, r => Assert.DoesNotContain("idx=8", r.Uri.Query));
    }

    // ---- hygiene --------------------------------------------------------------------------------

    [Fact]
    public async Task GetCatalog_NeverSendsCookiesAndSendsAppUserAgentOnly()
    {
        FakeHttpHandler fake = Fake(_ => FakeHttpHandler.Text(200, Readme()), _ => FakeHttpHandler.Json(200, Archive()));

        await Service(fake).GetCatalogAsync("en-US", CancellationToken.None);

        Assert.NotEmpty(fake.Requests);
        Assert.All(fake.Requests, r =>
        {
            Assert.Null(r.Header("Cookie"));
            string? ua = r.Header("User-Agent");
            Assert.NotNull(ua);
            Assert.StartsWith("BingWallpaperUpdater/", ua, StringComparison.Ordinal);
            // RecordedRequest joins the product and the "(+<repo>)" comment with ", "; parse the product alone.
            string product = ua.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)[0];
            Assert.True(ProductInfoHeaderValue.TryParse(product, out _));
            Assert.Contains("(+https://github.com/anhyeuviolet/BingWallpaperUpdater)", ua, StringComparison.Ordinal);
        });
    }
}
