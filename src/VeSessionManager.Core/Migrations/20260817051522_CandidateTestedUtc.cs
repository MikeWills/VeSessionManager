using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class CandidateTestedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TestedUtc",
                table: "Candidates",
                type: "TEXT",
                nullable: true);

            // **Deliberately not backfilled.** Every candidate who has already tested keeps a null
            // here, so a CandidateTested rule created later can never fire for them — the same
            // direction of safety as MessageRule.CreatedUtc, arrived at for free rather than by a
            // second guard. The values that could be backfilled (ResultMarkedUtc, the session's own
            // start) are each some other moment wearing this one's name, and a wrong timestamp here
            // is worse than none: it would let a rule reach a year of imported history.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TestedUtc",
                table: "Candidates");
        }
    }
}
