using System.Text.RegularExpressions;
using BingWallpaperUpdater.Core.Catalog;
using BingWallpaperUpdater.Core.Model;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// Regression baseline for the niumoo README format (PITFALLS P5): Fixtures/README.sample.md is a verbatim
/// copy of the live README fetched on 2026-09-20. If the upstream shape changes, refresh the fixture and
/// adjust the parser deliberately.
/// </summary>
public sealed class MarkdownCatalogParserTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "README.sample.md");

    private static string Fixture() => File.ReadAllText(FixturePath);

    [Fact]
    public void Parse_Fixture_YieldsAtLeastTwentyRows()
    {
        IReadOnlyList<CatalogEntry> rows = MarkdownCatalogParser.Parse(Fixture());

        Assert.True(rows.Count >= 20, $"expected >= 20 rows, got {rows.Count}");
    }

    [Fact]
    public void Parse_Fixture_AllIdsAreWellFormedAndParsable()
    {
        IReadOnlyList<CatalogEntry> rows = MarkdownCatalogParser.Parse(Fixture());

        Assert.All(rows, r =>
        {
            Assert.StartsWith("OHR.", r.Id.Value, StringComparison.Ordinal);
            Assert.True(ImageId.TryParse(r.Id.Value, out ImageId parsed));
            Assert.Equal(r.Id, parsed);
            Assert.False(string.IsNullOrEmpty(r.Id.Name));
            Assert.Equal("EN-US", r.Id.Market);
            Assert.Equal(CatalogSources.GitHub, r.Source);
        });
    }

    [Fact]
    public void Parse_Fixture_FirstRowIsTodayAndDatesAreIsoFormatted()
    {
        IReadOnlyList<CatalogEntry> rows = MarkdownCatalogParser.Parse(Fixture());

        Assert.StartsWith("OHR.", rows[0].Id.Value, StringComparison.Ordinal);
        Assert.All(rows, r => Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}$"), r.Date!));
    }

    [Fact]
    public void Parse_Fixture_EntriesAreUniqueById()
    {
        IReadOnlyList<CatalogEntry> rows = MarkdownCatalogParser.Parse(Fixture());

        Assert.Equal(rows.Count, rows.Select(r => r.Id.Value).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("# nothing here")]
    public void Parse_NoRows_ReturnsEmptyList(string markdown)
    {
        Assert.Empty(MarkdownCatalogParser.Parse(markdown));
    }

    [Fact]
    public void Parse_Null_ReturnsEmptyList()
    {
        Assert.Empty(MarkdownCatalogParser.Parse(null));
    }

    [Fact]
    public void Parse_ToleratesCrlfAndWwwHost()
    {
        string row = "|![](https://www.bing.com/th?id=OHR.TestImage_EN-US1234567890_UHD.jpg&pid=hp&w=384&h=216&rs=1&c=4)2026-09-19 [download 4k](https://www.bing.com/th?id=OHR.TestImage_EN-US1234567890_UHD.jpg&rf=LaDigue_UHD.jpg&pid=hp&w=3840&h=2160&rs=1&c=4)|\r\n";

        IReadOnlyList<CatalogEntry> rows = MarkdownCatalogParser.Parse(row + row);

        CatalogEntry entry = Assert.Single(rows);
        Assert.Equal("OHR.TestImage_EN-US1234567890", entry.Id.Value);
        Assert.Equal("2026-09-19", entry.Date);
    }

    [Fact]
    public void ParseToday_Fixture_ReturnsCopyright()
    {
        (ImageId Id, string Copyright)? today = MarkdownCatalogParser.ParseToday(Fixture());

        Assert.NotNull(today);
        Assert.StartsWith("OHR.", today.Value.Id.Value, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(today.Value.Copyright));
    }

    [Fact]
    public void ParseToday_MatchesFirstRow()
    {
        string markdown = Fixture();

        (ImageId Id, string Copyright)? today = MarkdownCatalogParser.ParseToday(markdown);
        IReadOnlyList<CatalogEntry> rows = MarkdownCatalogParser.Parse(markdown);

        Assert.NotNull(today);
        Assert.Equal(rows[0].Id, today.Value.Id);
    }
}
