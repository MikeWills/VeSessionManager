using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class AuditLogTeamAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TeamId",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TeamId",
                table: "AuditLogs",
                column: "TeamId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLogs_Teams_TeamId",
                table: "AuditLogs",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Backfill, so the fix reaches history and not only future rows.
            //
            // Without this, #86 part 3 would mean "a TeamAdmin can see background entries written
            // from now on" — while the entries anyone actually wants to review are the ones already
            // there. The statements below resolve a team for the rows that can have one: an entry
            // about a Session, a Candidate (through its session), or a Payment (through its
            // candidate's session).
            //
            // Scoped to `UserId IS NULL` deliberately. A user-attributed row already scopes through
            // the acting user's own team memberships, and filling this column there too would make
            // one question answerable two ways — see AuditLog.TeamId.
            //
            // Rows whose entity has since been deleted, and every VolunteerExaminer row, resolve to
            // nothing and stay null. That is exactly the pre-existing behaviour (SystemAdmin-only),
            // so the worst case here is unchanged rather than made worse.
            migrationBuilder.Sql("""
                UPDATE AuditLogs SET TeamId = (SELECT s.TeamId FROM Sessions s WHERE s.Id = AuditLogs.EntityId)
                WHERE UserId IS NULL AND EntityType = 'Session';
                """);
            migrationBuilder.Sql("""
                UPDATE AuditLogs SET TeamId = (
                    SELECT s.TeamId FROM Candidates c JOIN Sessions s ON s.Id = c.SessionId WHERE c.Id = AuditLogs.EntityId)
                WHERE UserId IS NULL AND EntityType = 'Candidate';
                """);
            migrationBuilder.Sql("""
                UPDATE AuditLogs SET TeamId = (
                    SELECT s.TeamId FROM Payments p
                    JOIN Candidates c ON c.Id = p.CandidateId
                    JOIN Sessions s ON s.Id = c.SessionId
                    WHERE p.Id = AuditLogs.EntityId)
                WHERE UserId IS NULL AND EntityType = 'Payment';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLogs_Teams_TeamId",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_TeamId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "AuditLogs");
        }
    }
}
