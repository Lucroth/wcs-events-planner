using System.Text.Json;
using WcsEvents.Sync.Crawl;
using WcsEvents.Sync.Wsdc;

namespace WcsEvents.Tests;

public class WsdcJsonTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    [Fact]
    public void DancerResponse_ParsesPlacementsIncludingFinalists()
    {
        var dto = JsonSerializer.Deserialize<DancerResponseDto>(Fixture("dancer-with-finalists.json"))!;

        Assert.Equal("dancer", dto.Type);
        Assert.Equal(10001, dto.Dancer.Wscid);

        var divisions = dto.Placements![DancerIngest.WestCoastSwing];
        var novice = divisions["NOV"];

        Assert.Equal("Novice", novice.Division.Name);
        Assert.Equal(19, novice.TotalPoints);
        Assert.Equal(19, novice.Competitions.Sum(c => c.Points));
        Assert.Contains(novice.Competitions, c => c.Result == "F");
        Assert.All(novice.Competitions, c => Assert.Equal("follower", c.Role));
    }

    [Fact]
    public void DancerResponse_TreatsAnEmptyPlacementsArrayAsNoPlacements()
    {
        // The registry sends "placements":[] rather than an empty object for a dancer with no points.
        var dto = JsonSerializer.Deserialize<DancerResponseDto>(Fixture("dancer-no-placements.json"))!;

        Assert.Equal("dancer", dto.Type);
        Assert.Null(dto.Placements);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("5.0", 5)]
    [InlineData("4.6", 5)]
    [InlineData("\"7\"", 7)]
    [InlineData("\"\"", 0)]
    [InlineData("null", 0)]
    public void Competition_ReadsAnOddPointsValueInsteadOfFailingTheDancer(string points, int expected)
    {
        var json = "{\"role\":\"leader\",\"points\":" + points
            + ",\"event\":{\"id\":1,\"name\":\"Example Open\",\"location\":null,\"url\":null,\"date\":\"May 2024\"},\"result\":\"F\"}";

        Assert.Equal(expected, JsonSerializer.Deserialize<CompetitionDto>(json)!.Points);
    }

    [Fact]
    public void Competition_StillRejectsPointsThatAreNotAScalar()
    {
        var json = "{\"role\":\"leader\",\"points\":[1],\"event\":{\"id\":1,\"name\":\"Example Open\",\"date\":\"May 2024\"},\"result\":\"F\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CompetitionDto>(json));
    }

    [Fact]
    public void NamesResponse_ParsesTheAutocompleteList()
    {
        var dto = JsonSerializer.Deserialize<NamesResponseDto>(Fixture("names.json"))!;

        Assert.Equal("names", dto.Type);
        Assert.NotEmpty(dto.Names);
        Assert.All(dto.Names, n => Assert.True(n.Wscid > 0));
    }

    [Theory]
    [InlineData("August 2018", 2018, 8)]
    [InlineData("December 2013", 2013, 12)]
    public void MonthYear_ParsesRegistryDates(string raw, int year, int month) =>
        Assert.Equal(new DateOnly(year, month, 1), MonthYear.Parse(raw));

    [Theory]
    [InlineData("")]
    [InlineData("someday")]
    [InlineData(null)]
    public void MonthYear_ReturnsNullForUnparseableDates(string? raw) =>
        Assert.Null(MonthYear.Parse(raw));
}
