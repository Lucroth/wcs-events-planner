using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WcsEvents.Sync.Data;
using WcsEvents.Sync.Scoring;

namespace WcsEvents.Tests;

public sealed class ScoringStoreTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly DbContextOptions<AppDbContext> options;

    public ScoringStoreTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
    }

    private AppDbContext NewContext() => new(options);

    private static ParsedRound Prelim() =>
        ScoringParser.ParseRound(File.ReadAllText(Path.Combine("Fixtures", "scoring-prelim.html")))!;

    private async Task StorePrelimAsync()
    {
        await using var db = NewContext();
        db.ScoringRounds.Add(ScoringStore.ToEntity(201, 3343, "Novice Jack&Jill prelim", Prelim()));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ToEntity_StoresTheRoundAndItsWholeField()
    {
        await StorePrelimAsync();

        await using var db = NewContext();
        var round = await db.ScoringRounds.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("NOV", round.DivisionAbbreviation);
        Assert.Equal(RoundKind.Prelim, round.Kind);
        Assert.Equal(new DateOnly(2025, 3, 27), round.EventDate);
        Assert.Equal(202, await db.ScoringEntries.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InferRoles_LabelsEachPrelimTableFromHowItsDancersUsuallyCompete()
    {
        await StorePrelimAsync();

        // Three dancers from the first table have a leader history in the mirror, one from the second
        // has a follower history. That majority decides each table.
        await SeedRoleHistoryAsync((21723, Role.Leader), (21682, Role.Leader), (12758, Role.Leader), (9094, Role.Follower));

        await using var db = NewContext();
        var labelled = await ScoringStore.InferRolesAsync(db, TestContext.Current.CancellationToken);

        Assert.Equal(202, labelled);

        var entries = await db.ScoringEntries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(entries, e => Assert.NotNull(e.Role));

        // Every row in a table shares its role, including dancers with no registry entry.
        var firstTable = entries.Where(e => e.TableIndex == 0).ToList();
        Assert.All(firstTable, e => Assert.Equal(Role.Leader, e.Role));
        Assert.Contains(firstTable, e => e.Wscid is null);

        Assert.All(entries.Where(e => e.TableIndex == 1), e => Assert.Equal(Role.Follower, e.Role));
    }

    [Fact]
    public async Task InferRoles_IgnoresTheSameBibOnADifferentDancer()
    {
        // Westie Harbor 2026 numbered each division from 1: bib 5 is a leader in the Novice final and
        // a follower in the Intermediate prelim. The final must not label the prelim table.
        await using (var seed = NewContext())
        {
            ScoringRound Round(int id, string name, RoundKind kind) => new()
            {
                Id = id, ScoringEventId = 7, EventName = "Harbor", RoundName = name, DivisionAbbreviation = name[..3].ToUpperInvariant(),
                IsJackAndJill = true, Kind = kind, EventDate = new DateOnly(2026, 10, 1),
            };
            seed.ScoringRounds.AddRange(Round(1, "Nov Jack&Jill final", RoundKind.Final), Round(2, "Int Jack&Jill prelim", RoundKind.Prelim));
            seed.ScoringEntries.AddRange(
                new ScoringEntry { RoundId = 1, Name = "Adam Lead", Bib = "5", Role = Role.Leader, Position = 1 },
                new ScoringEntry { RoundId = 1, Name = "Ewa Follow", Role = Role.Follower, Position = 1 },
                new ScoringEntry { RoundId = 2, Name = "Basia Follow", Bib = "5", Wscid = 9094, Position = 1 },
                new ScoringEntry { RoundId = 2, Name = "Kasia Follow", Bib = "6", Position = 2 });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SeedRoleHistoryAsync((9094, Role.Follower));

        await using var db = NewContext();
        await ScoringStore.InferRolesAsync(db, TestContext.Current.CancellationToken);

        Assert.All(
            await db.ScoringEntries.AsNoTracking().Where(e => e.RoundId == 2).ToListAsync(TestContext.Current.CancellationToken),
            e => Assert.Equal(Role.Follower, e.Role));
    }

    [Fact]
    public async Task InferRoles_LeavesATableUnlabelledWhenTheMirrorGivesNoMajority()
    {
        await StorePrelimAsync();
        await SeedRoleHistoryAsync((21723, Role.Leader), (21682, Role.Follower));

        await using var db = NewContext();
        await ScoringStore.InferRolesAsync(db, TestContext.Current.CancellationToken);

        // Both known dancers sit in table 0 and disagree, so that table stays unlabelled.
        Assert.All(
            await db.ScoringEntries.AsNoTracking().Where(e => e.TableIndex == 0).ToListAsync(TestContext.Current.CancellationToken),
            e => Assert.Null(e.Role));
    }

    [Fact]
    public async Task SyncEventsAsync_RecoversPastEventsFromTheRoundsAlreadyMirrored()
    {
        await StorePrelimAsync();

        await using var db = NewContext();
        await ScoringStore.SyncEventsAsync(db, [], TestContext.Current.CancellationToken);

        var stored = await db.ScoringEvents.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(201, stored.Id);
        Assert.Equal("UK West Coast Swing Championships 2025", stored.Name);
        Assert.Equal(new DateOnly(2025, 3, 27), stored.DateFrom);
        Assert.Equal("London", stored.City);
        Assert.Equal("United Kingdom", stored.Country);
    }

    [Fact]
    public async Task SyncEventsAsync_LetsTheListingCorrectWhatTheRoundsImplied()
    {
        await StorePrelimAsync();

        ParsedEvent listed = new(
            Id: 201,
            Name: "UK West Coast Swing Championships 2025",
            DateFrom: new DateOnly(2025, 3, 26),
            DateTo: new DateOnly(2025, 3, 30),
            City: "London",
            CountryCode: "GBR",
            IsWsdc: true,
            TicketUrl: "https://ukwcs.example/register");

        await using var db = NewContext();
        await ScoringStore.SyncEventsAsync(db, [listed], TestContext.Current.CancellationToken);

        var stored = await db.ScoringEvents.SingleAsync(TestContext.Current.CancellationToken);

        // A round only knows the day it was danced; the event knows when it opened and closed.
        Assert.Equal(new DateOnly(2025, 3, 26), stored.DateFrom);
        Assert.Equal(new DateOnly(2025, 3, 30), stored.DateTo);
        Assert.Equal("United Kingdom", stored.Country);
        Assert.True(stored.IsWsdc);
        Assert.Equal("https://ukwcs.example/register", stored.TicketUrl);
    }

    [Fact]
    public async Task SyncEventsAsync_RunsTwiceWithoutDuplicatingAnything()
    {
        await StorePrelimAsync();

        await using var db = NewContext();
        await ScoringStore.SyncEventsAsync(db, [], TestContext.Current.CancellationToken);
        await ScoringStore.SyncEventsAsync(db, [], TestContext.Current.CancellationToken);

        Assert.Equal(1, await db.ScoringEvents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void InheritCities_FillsAMissingCityFromTheLatestEarlierEdition()
    {
        ScoringEvent older = new() { Id = 188, Name = "SwingVester 2024/25", DateFrom = new DateOnly(2024, 12, 27), City = "Linz", Country = "Austria" };
        ScoringEvent last = new() { Id = 349, Name = "SwingVester 2025/26 WSDC", DateFrom = new DateOnly(2025, 12, 31), City = "Wels", Country = "Austria" };
        ScoringEvent next = new() { Id = 350, Name = "SwingVester 2026/27 WSDC", DateFrom = new DateOnly(2026, 12, 31), Country = "Austria" };
        ScoringEvent other = new() { Id = 1, Name = "Budafest 2027", DateFrom = new DateOnly(2027, 1, 6) };

        ScoringStore.InheritCities([older, last, next, other]);

        Assert.Equal("Wels", next.City);
        Assert.Null(other.City);
    }

    [Fact]
    public void ColumnsTrusted_RejectsAPairingWhoseLeaderColumnHoldsFollowers()
    {
        Dictionary<int, Role> usual = new() { [1] = Role.Follower, [2] = Role.Follower, [3] = Role.Follower, [4] = Role.Follower, [5] = Role.Leader, [6] = Role.Leader };

        Assert.False(ScoringStore.ColumnsTrusted([(Role.Leader, 1), (Role.Leader, 2), (Role.Leader, 3), (Role.Follower, 5), (Role.Follower, 6)], usual));
        Assert.True(ScoringStore.ColumnsTrusted([(Role.Leader, 5), (Role.Leader, 6), (Role.Follower, 1), (Role.Follower, 2), (Role.Follower, 3)], usual));
        Assert.True(ScoringStore.ColumnsTrusted([(Role.Leader, 1), (Role.Follower, 5)], usual));
    }

    [Fact]
    public void InheritCities_ReplacesAPlaceholderCityFromAnotherCountry()
    {
        ScoringEvent rolling = new() { Id = 146, Name = "Rolling Swing 2024", DateFrom = new DateOnly(2024, 8, 29), City = "Bron", Country = "France" };
        ScoringEvent last = new() { Id = 300, Name = "Scandinavian Open 2025 [SNOW]", DateFrom = new DateOnly(2025, 10, 29), City = "Stockholm", Country = "Sweden" };
        ScoringEvent next = new() { Id = 433, Name = "Scandinavian Open 2026 [SNOW]", DateFrom = new DateOnly(2026, 10, 28), City = "Bron", Country = "Sweden" };
        ScoringEvent defaulted = new() { Id = 396, Name = "Westie Joy 2026", DateFrom = new DateOnly(2026, 8, 21), City = "Rome", Country = "Romania" };
        ScoringEvent rome = new() { Id = 2, Name = "Swing In Capital 2026", DateFrom = new DateOnly(2026, 4, 9), City = "Rome", Country = "Italy" };

        ScoringStore.InheritCities([rolling, last, next, defaulted, rome]);

        Assert.Equal("Stockholm", next.City);
        Assert.Equal("Bron", rolling.City);
        Assert.Null(defaulted.City);
        Assert.Equal("Rome", rome.City);
    }

    private Task SeedRoleHistoryAsync(params (int Wscid, Role Role)[] dancers) =>
        SeedRoleHistoryAsync(divisionId: 4, dancers);

    private async Task SeedRoleHistoryAsync(int divisionId, params (int Wscid, Role Role)[] dancers)
    {
        await using var db = NewContext();

        foreach (var (wscid, role) in dancers)
        {
            db.Dancers.Add(new Dancer { Wscid = wscid, FirstName = "Test", LastName = $"D{wscid}" });
            db.Placements.Add(new Placement
            {
                DancerWscid = wscid,
                EventId = 1,
                DivisionId = divisionId,
                Role = role,
                DateRaw = "March 2025",
                Date = new DateOnly(2025, 3, 1),
                EventName = "Seed",
                Points = 1,
                ResultRaw = "F",
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose() => connection.Dispose();
}
