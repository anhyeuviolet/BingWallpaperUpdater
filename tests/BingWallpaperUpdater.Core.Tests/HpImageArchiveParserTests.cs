using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// Regression baseline for the Bing HPImageArchive JSON (RESEARCH Spike 2): Fixtures/hpimagearchive.sample.json is
/// the verbatim body of <c>HPImageArchive.aspx?format=js&amp;idx=0&amp;n=8&amp;mkt=en-US</c> captured 2026-09-20.
/// </summary>
public sealed class HpImageArchiveParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Parse_Fixture_ReturnsEightEntriesWithMetadata()
    {
        IReadOnlyList<CatalogEntry> entries = HpImageArchiveParser.Parse(Fixture("hpimagearchive.sample.json"));

        Assert.Equal(8, entries.Count);
        Assert.Equal("OHR.AlphornBavaria_EN-US6200857270", entries[0].Id.Value);
        Assert.All(entries, e =>
        {
            Assert.Equal(CatalogSources.HpImageArchive, e.Source);
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.False(string.IsNullOrWhiteSpace(e.Copyright));
            Assert.NotNull(e.StartDate);
            Assert.Matches(@"^\d{8}$", e.StartDate);
            Assert.NotNull(e.Date);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", e.Date);
        });
        // IN-11: Date is Bing's enddate in README shape — the same date README.sample.md carries for these IDs.
        Assert.Equal("2026-09-20", entries[0].Date);
        Assert.Equal("2026-09-17", entries.Single(e => e.Id.Value == "OHR.IcyCubs_EN-US5222104616").Date);
        Assert.Equal("2026-09-14", entries.Single(e => e.Id.Value == "OHR.MisurinaPeak_EN-US4897144498").Date);
    }

    [Theory]
    [InlineData("{\"images\":[{\"urlbase\":\"/th?id=OHR.NoEnd_EN-US1111111111\"}]}")]
    [InlineData("{\"images\":[{\"enddate\":\"\",\"urlbase\":\"/th?id=OHR.NoEnd_EN-US1111111111\"}]}")]
    [InlineData("{\"images\":[{\"enddate\":\"2026-09-20\",\"urlbase\":\"/th?id=OHR.NoEnd_EN-US1111111111\"}]}")]
    [InlineData("{\"images\":[{\"enddate\":\"2026092\",\"urlbase\":\"/th?id=OHR.NoEnd_EN-US1111111111\"}]}")]
    [InlineData("{\"images\":[{\"enddate\":\"2026092O\",\"urlbase\":\"/th?id=OHR.NoEnd_EN-US1111111111\"}]}")]
    public void Parse_MissingOrMalformedEndDate_DateStaysNull(string json)
    {
        CatalogEntry entry = Assert.Single(HpImageArchiveParser.Parse(json));

        Assert.Null(entry.Date);
    }

    [Fact]
    public void Parse_NullLiteralFixture_ReturnsEmptyList()
    {
        string body = Fixture("null.json");

        Assert.Equal("null", body);
        Assert.Empty(HpImageArchiveParser.Parse(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"images\":[]}")]
    [InlineData("{\"images\":null}")]
    public void Parse_EmptyShapes_ReturnEmptyList(string? json)
    {
        Assert.Empty(HpImageArchiveParser.Parse(json));
    }

    [Theory]
    [InlineData("{\"images\":[")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"images\":\"oops\"}")]
    public void Parse_MalformedJson_ReturnsEmptyListWithoutThrowing(string json)
    {
        Assert.Empty(HpImageArchiveParser.Parse(json));
    }

    [Fact]
    public void Parse_DuplicateUrlbase_FirstOccurrenceWins()
    {
        const string json = """
            {"images":[
              {"startdate":"20260919","urlbase":"/th?id=OHR.Dup_EN-US1111111111","title":"first","copyright":"c1"},
              {"startdate":"20260918","urlbase":"/th?id=OHR.Dup_EN-US1111111111","title":"second","copyright":"c2"}
            ]}
            """;

        IReadOnlyList<CatalogEntry> entries = HpImageArchiveParser.Parse(json);

        CatalogEntry only = Assert.Single(entries);
        Assert.Equal("first", only.Title);
        Assert.Equal("c1", only.Copyright);
        Assert.Equal("20260919", only.StartDate);
    }

    [Theory]
    [InlineData("{\"images\":[{\"urlbase\":\"/th?id=OHR.Blank_EN-US1111111111\"}]}")]
    [InlineData("{\"images\":[{\"urlbase\":\"/th?id=OHR.Blank_EN-US1111111111\",\"title\":\"\",\"copyright\":\"\",\"copyrightlink\":\"\"}]}")]
    [InlineData("{\"images\":[{\"urlbase\":\"/th?id=OHR.Blank_EN-US1111111111\",\"title\":\"  \",\"copyright\":\"\\t\",\"copyrightlink\":\" \"}]}")]
    [InlineData("{\"images\":[{\"urlbase\":\"/th?id=OHR.Blank_EN-US1111111111\",\"title\":null,\"copyright\":null,\"copyrightlink\":null}]}")]
    public void Parse_BlankTitleOrCopyright_StoredAsNull(string json)
    {
        CatalogEntry entry = Assert.Single(HpImageArchiveParser.Parse(json));

        Assert.Null(entry.Title);
        Assert.Null(entry.Copyright);
        Assert.Null(entry.CopyrightLink);
    }

    /// <summary>
    /// SRC-04: a ROW row as returned live for mkt=en-AU / vi-VN (2026-09-21). The ID parses, the literal title "Info"
    /// is stored as null (blank in the window), and the real copyright is kept.
    /// </summary>
    [Theory]
    [InlineData("{\"images\":[{\"startdate\":\"20260920\",\"enddate\":\"20260921\",\"urlbase\":\"/th?id=OHR.ParisSunset_ROW1775373883\",\"title\":\"Info\",\"copyright\":\"Eiffel Tower at sunset, Paris, France (\\u00a9 Alexander Spatari/Getty Images)\",\"copyrightlink\":\"https://www.bing.com/search?q=Eiffel+Tower\"}]}")]
    [InlineData("{\"images\":[{\"enddate\":\"20260921\",\"urlbase\":\"/th?id=OHR.ParisSunset_ROW1775373883\",\"title\":\" Info \",\"copyright\":\"Eiffel Tower at sunset, Paris, France (\\u00a9 Alexander Spatari/Getty Images)\"}]}")]
    public void Parse_RowRow_ParsesWithNullTitleAndKeptCopyright(string json)
    {
        CatalogEntry entry = Assert.Single(HpImageArchiveParser.Parse(json));

        Assert.Equal("OHR.ParisSunset_ROW1775373883", entry.Id.Value);
        Assert.Equal("ROW", entry.Id.Market);
        Assert.Null(entry.Title);
        Assert.Equal("Eiffel Tower at sunset, Paris, France (© Alexander Spatari/Getty Images)", entry.Copyright);
        Assert.Equal("2026-09-21", entry.Date);
        Assert.Equal(CatalogSources.HpImageArchive, entry.Source);
    }

    /// <summary>Only the exact literal "Info" is dropped; a real title (including one that merely contains or resembles it) is kept unchanged.</summary>
    [Theory]
    [InlineData("The tower that won Paris over", "The tower that won Paris over")]
    [InlineData("Info about Paris", "Info about Paris")]
    [InlineData("info", "info")]
    [InlineData("Information", "Information")]
    public void Parse_RealTitle_IsKeptUnchanged(string title, string expected)
    {
        string json = "{\"images\":[{\"urlbase\":\"/th?id=OHR.ParisSunset_ROW1775373883\",\"title\":\"" + title + "\",\"copyright\":\"c\"}]}";

        CatalogEntry entry = Assert.Single(HpImageArchiveParser.Parse(json));

        Assert.Equal(expected, entry.Title);
        Assert.Equal("c", entry.Copyright);
    }

    [Fact]
    public void Parse_KeepsStartDateAsRawString()
    {
        const string json = "{\"images\":[{\"startdate\":\"00000101\",\"enddate\":\"99991231\",\"urlbase\":\"/th?id=OHR.Raw_EN-US1111111111\"}]}";

        CatalogEntry entry = Assert.Single(HpImageArchiveParser.Parse(json));

        Assert.Equal("00000101", entry.StartDate);
        Assert.Equal("9999-12-31", entry.Date);   // shape only, never parsed as a calendar date
    }

    [Fact]
    public void Parse_SkipsImagesWithUnparsableIds()
    {
        const string json = """
            {"images":[
              {"urlbase":"/th?id=OHR.Good_EN-US1111111111"},
              {"urlbase":"/th?id=NOTOHR.Bad_EN-US2222222222"},
              {"urlbase":"https://evil.example/th?id=OHR.Evil_EN-US3333333333"},
              {"urlbase":null},
              {"urlbase":"/th?id=OHR.Also_EN-US4444444444"}
            ]}
            """;

        IReadOnlyList<CatalogEntry> entries = HpImageArchiveParser.Parse(json);

        Assert.Equal(new[] { "OHR.Good_EN-US1111111111", "OHR.Also_EN-US4444444444" }, entries.Select(e => e.Id.Value));
    }

    [Fact]
    public void Parse_OrderIsSourceOrderAndStableAcrossRepeatedParses()
    {
        string json = Fixture("hpimagearchive.sample.json");

        var first = HpImageArchiveParser.Parse(json).Select(e => e.Id.Value).ToArray();
        var second = HpImageArchiveParser.Parse(json).Select(e => e.Id.Value).ToArray();

        Assert.Equal(first, second);
        Assert.Equal(first.Length, first.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("OHR.AlphornBavaria_EN-US6200857270", first[0]);
    }
}
