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
            Assert.Null(e.Date);
        });
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

    [Fact]
    public void Parse_KeepsStartDateAsRawString()
    {
        const string json = "{\"images\":[{\"startdate\":\"00000101\",\"enddate\":\"99991231\",\"urlbase\":\"/th?id=OHR.Raw_EN-US1111111111\"}]}";

        CatalogEntry entry = Assert.Single(HpImageArchiveParser.Parse(json));

        Assert.Equal("00000101", entry.StartDate);
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
