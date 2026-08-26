using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Adds <c>Payment.CandidateNameSnapshot</c> — the name a Payment was for, captured once so a
    /// financial-transactions report survives PII purge clearing <c>Candidate.Name</c> (issue
    /// discussed 2026-08-26). Unlike <c>MessageEligibilityFloor</c>/<c>RemovePaymentExpiredUnpaid</c>,
    /// this backfill is a real one, not a fabrication: for every existing Payment whose Candidate
    /// hasn't been purged yet, the name is still sitting right there and is simply being copied onto
    /// the row that will actually need it once it's gone. A Payment whose candidate was already
    /// purged stays null — there's nothing left to copy, and null correctly means "unknown," not
    /// "never had a name."
    /// </summary>
    public partial class AddPaymentCandidateNameSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CandidateNameSnapshot",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE Payments
                SET CandidateNameSnapshot = (SELECT Name FROM Candidates WHERE Candidates.Id = Payments.CandidateId)
                WHERE CandidateNameSnapshot IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CandidateNameSnapshot",
                table: "Payments");
        }
    }
}
