using Microsoft.EntityFrameworkCore;

namespace WcsEvents.Sync.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Dancer> Dancers => Set<Dancer>();
    public DbSet<Division> Divisions => Set<Division>();
    public DbSet<Placement> Placements => Set<Placement>();
    public DbSet<CrawlState> CrawlStates => Set<CrawlState>();
    public DbSet<ScoringRound> ScoringRounds => Set<ScoringRound>();
    public DbSet<ScoringEntry> ScoringEntries => Set<ScoringEntry>();
    public DbSet<JudgeMark> JudgeMarks => Set<JudgeMark>();
    public DbSet<ScoringEvent> ScoringEvents => Set<ScoringEvent>();
    public DbSet<PublishedDoc> PublishedDocs => Set<PublishedDoc>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Dancer>(e =>
        {
            e.HasKey(d => d.Wscid);
            e.Property(d => d.Wscid).ValueGeneratedNever();
            e.HasIndex(d => new { d.LastName, d.FirstName });
            e.HasIndex(d => d.LastCrawledUtc);
        });

        b.Entity<Division>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).ValueGeneratedNever();
            e.HasIndex(d => d.Abbreviation).IsUnique();
            e.HasData(Divisions_Seed);
        });

        b.Entity<Placement>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.EventId, p.DateRaw, p.DivisionId, p.Role });
            e.HasIndex(p => new { p.DivisionId, p.Role, p.DancerWscid });
            e.HasIndex(p => p.DancerWscid);
            e.HasOne(p => p.Dancer).WithMany(d => d.Placements).HasForeignKey(p => p.DancerWscid).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Division).WithMany().HasForeignKey(p => p.DivisionId);
        });

        b.Entity<CrawlState>().HasKey(c => c.Id);

        b.Entity<ScoringRound>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.HasIndex(r => r.ScoringEventId);
            e.HasIndex(r => new { r.DivisionAbbreviation, r.IsJackAndJill, r.Kind });
        });

        b.Entity<ScoringEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Wscid);
            e.HasIndex(x => new { x.RoundId, x.TableIndex });
            e.HasOne(x => x.Round).WithMany(r => r.Entries).HasForeignKey(x => x.RoundId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JudgeMark>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => m.EntryId);
            e.HasIndex(m => m.Judge);
            e.HasOne(m => m.Entry).WithMany(x => x.Marks).HasForeignKey(m => m.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PublishedDoc>().HasKey(d => d.Path);

        b.Entity<ScoringEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.DateFrom);
        });

    }

    /// <summary>WSDC division ids are stable and small; seeded so SortOrder (ladder position) is known up front.</summary>
    private static readonly Division[] Divisions_Seed =
    [
        new() { Id = 1, Name = "Juniors", Abbreviation = "JRS", SortOrder = null },
        new() { Id = 2, Name = "Masters", Abbreviation = "MSTR", SortOrder = null },
        new() { Id = 3, Name = "Newcomer", Abbreviation = "NEW", SortOrder = 0 },
        new() { Id = 4, Name = "Novice", Abbreviation = "NOV", SortOrder = 1 },
        new() { Id = 5, Name = "Intermediate", Abbreviation = "INT", SortOrder = 2 },
        new() { Id = 6, Name = "Advanced", Abbreviation = "ADV", SortOrder = 3 },
        new() { Id = 7, Name = "Champions", Abbreviation = "CHMP", SortOrder = 5 },
        new() { Id = 8, Name = "All-Stars", Abbreviation = "ALS", SortOrder = 4 },
        new() { Id = 9, Name = "Invitational", Abbreviation = "INV", SortOrder = null },
        new() { Id = 10, Name = "Professional", Abbreviation = "PRO", SortOrder = null },
    ];
}
