using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace WcsEvents.Sync.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CrawlStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastAttemptedWscid = table.Column<int>(type: "INTEGER", nullable: false),
                    HighestFoundWscid = table.Column<int>(type: "INTEGER", nullable: false),
                    LastFullCrawlCompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastScoringSyncUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrawlStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dancers",
                columns: table => new
                {
                    Wscid = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", nullable: false),
                    LastName = table.Column<string>(type: "TEXT", nullable: false),
                    LastCrawledUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dancers", x => x.Wscid);
                });

            migrationBuilder.CreateTable(
                name: "Divisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Abbreviation = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Divisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublishedDocs",
                columns: table => new
                {
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishedDocs", x => x.Path);
                });

            migrationBuilder.CreateTable(
                name: "ScoringEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DateFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DateTo = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    City = table.Column<string>(type: "TEXT", nullable: true),
                    Country = table.Column<string>(type: "TEXT", nullable: true),
                    IsWsdc = table.Column<bool>(type: "INTEGER", nullable: false),
                    TicketUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScoringEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScoringRounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    ScoringEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    EventName = table.Column<string>(type: "TEXT", nullable: false),
                    EventDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    Country = table.Column<string>(type: "TEXT", nullable: true),
                    RoundName = table.Column<string>(type: "TEXT", nullable: false),
                    DivisionAbbreviation = table.Column<string>(type: "TEXT", nullable: true),
                    IsJackAndJill = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScoringRounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Placements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DancerWscid = table.Column<int>(type: "INTEGER", nullable: false),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    DivisionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    DateRaw = table.Column<string>(type: "TEXT", nullable: false),
                    EventName = table.Column<string>(type: "TEXT", nullable: false),
                    EventLocation = table.Column<string>(type: "TEXT", nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Points = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultRaw = table.Column<string>(type: "TEXT", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Placements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Placements_Dancers_DancerWscid",
                        column: x => x.DancerWscid,
                        principalTable: "Dancers",
                        principalColumn: "Wscid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Placements_Divisions_DivisionId",
                        column: x => x.DivisionId,
                        principalTable: "Divisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScoringEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoundId = table.Column<int>(type: "INTEGER", nullable: false),
                    Wscid = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Bib = table.Column<string>(type: "TEXT", nullable: true),
                    Role = table.Column<int>(type: "INTEGER", nullable: true),
                    TableIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Score = table.Column<string>(type: "TEXT", nullable: true),
                    Advanced = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAlternate = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsScratched = table.Column<bool>(type: "INTEGER", nullable: false),
                    LinkedByMatch = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScoringEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScoringEntries_ScoringRounds_RoundId",
                        column: x => x.RoundId,
                        principalTable: "ScoringRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JudgeMarks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Judge = table.Column<string>(type: "TEXT", nullable: false),
                    Mark = table.Column<string>(type: "TEXT", nullable: false),
                    Points = table.Column<double>(type: "REAL", nullable: true),
                    Placement = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JudgeMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JudgeMarks_ScoringEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "ScoringEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Divisions",
                columns: new[] { "Id", "Abbreviation", "Name", "SortOrder" },
                values: new object[,]
                {
                    { 1, "JRS", "Juniors", null },
                    { 2, "MSTR", "Masters", null },
                    { 3, "NEW", "Newcomer", 0 },
                    { 4, "NOV", "Novice", 1 },
                    { 5, "INT", "Intermediate", 2 },
                    { 6, "ADV", "Advanced", 3 },
                    { 7, "CHMP", "Champions", 5 },
                    { 8, "ALS", "All-Stars", 4 },
                    { 9, "INV", "Invitational", null },
                    { 10, "PRO", "Professional", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dancers_LastCrawledUtc",
                table: "Dancers",
                column: "LastCrawledUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dancers_LastName_FirstName",
                table: "Dancers",
                columns: new[] { "LastName", "FirstName" });

            migrationBuilder.CreateIndex(
                name: "IX_Divisions_Abbreviation",
                table: "Divisions",
                column: "Abbreviation",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JudgeMarks_EntryId",
                table: "JudgeMarks",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_JudgeMarks_Judge",
                table: "JudgeMarks",
                column: "Judge");

            migrationBuilder.CreateIndex(
                name: "IX_Placements_DancerWscid",
                table: "Placements",
                column: "DancerWscid");

            migrationBuilder.CreateIndex(
                name: "IX_Placements_DivisionId_Role_DancerWscid",
                table: "Placements",
                columns: new[] { "DivisionId", "Role", "DancerWscid" });

            migrationBuilder.CreateIndex(
                name: "IX_Placements_EventId_DateRaw_DivisionId_Role",
                table: "Placements",
                columns: new[] { "EventId", "DateRaw", "DivisionId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringEntries_RoundId_TableIndex",
                table: "ScoringEntries",
                columns: new[] { "RoundId", "TableIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringEntries_Wscid",
                table: "ScoringEntries",
                column: "Wscid");

            migrationBuilder.CreateIndex(
                name: "IX_ScoringEvents_DateFrom",
                table: "ScoringEvents",
                column: "DateFrom");

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRounds_DivisionAbbreviation_IsJackAndJill_Kind",
                table: "ScoringRounds",
                columns: new[] { "DivisionAbbreviation", "IsJackAndJill", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ScoringRounds_ScoringEventId",
                table: "ScoringRounds",
                column: "ScoringEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrawlStates");

            migrationBuilder.DropTable(
                name: "JudgeMarks");

            migrationBuilder.DropTable(
                name: "Placements");

            migrationBuilder.DropTable(
                name: "PublishedDocs");

            migrationBuilder.DropTable(
                name: "ScoringEvents");

            migrationBuilder.DropTable(
                name: "ScoringEntries");

            migrationBuilder.DropTable(
                name: "Dancers");

            migrationBuilder.DropTable(
                name: "Divisions");

            migrationBuilder.DropTable(
                name: "ScoringRounds");
        }
    }
}
