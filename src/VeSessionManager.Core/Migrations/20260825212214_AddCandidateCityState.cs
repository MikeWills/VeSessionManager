using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Adds <c>Candidate.City</c>/<c>State</c> — issue #463, "who's local," a City column on the
    /// session candidate list. Sourced from ExamTools' <c>city</c>/<c>state</c> registration fields
    /// (confirmed present on <c>export/basic.json</c>); ingestion backfills them on the next poll for
    /// existing candidates, the same self-healing pattern every other field on this table follows —
    /// no data migration needed here, only the schema.
    /// </summary>
    public partial class AddCandidateCityState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Candidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "Candidates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "City",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "State",
                table: "Candidates");
        }
    }
}
