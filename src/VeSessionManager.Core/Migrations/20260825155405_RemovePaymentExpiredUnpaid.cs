using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Drops <c>Payments.ExpiredUnpaid</c>. It tracked whether this team's own exam/retest fee had
    /// gone unpaid for 10 days — a state that cannot legitimately arise, since payment is required
    /// before testing (enforced by the VE running the session, not by this app). Mike, 2026-08-25:
    /// "the only '10 day rule' is the lifetime of the application at the FCC ... any fees related to
    /// a test must be collected prior to the test. No fee, no test." See
    /// <c>PaymentReminderService</c>'s own summary and CLAUDE.md's Known Constraints.
    /// </summary>
    public partial class RemovePaymentExpiredUnpaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiredUnpaid",
                table: "Payments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExpiredUnpaid",
                table: "Payments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
