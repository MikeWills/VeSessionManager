using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class SchemaHygieneIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_TeamId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_JobRunHistories_TeamId",
                table: "JobRunHistories");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TeamId_ScheduledStartUtc",
                table: "Sessions",
                columns: new[] { "TeamId", "ScheduledStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SquarePaymentReferenceId",
                table: "Payments",
                column: "SquarePaymentReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRunHistories_TeamId_JobName_StartedUtc",
                table: "JobRunHistories",
                columns: new[] { "TeamId", "JobName", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_ApplicationStatus",
                table: "Candidates",
                column: "ApplicationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TimestampUtc",
                table: "AuditLogs",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_TeamId_ScheduledStartUtc",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Payments_SquarePaymentReferenceId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_JobRunHistories_TeamId_JobName_StartedUtc",
                table: "JobRunHistories");

            migrationBuilder.DropIndex(
                name: "IX_Candidates_ApplicationStatus",
                table: "Candidates");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_TimestampUtc",
                table: "AuditLogs");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TeamId",
                table: "Sessions",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRunHistories_TeamId",
                table: "JobRunHistories",
                column: "TeamId");
        }
    }
}
