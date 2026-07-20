using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase6_5MultiTeamFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ExamToolsTeamCode = table.Column<string>(type: "TEXT", nullable: true),
                    ExamToolsUsername = table.Column<string>(type: "TEXT", nullable: true),
                    ExamToolsPassword = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            // Seed the one team that already exists in practice (single-tenant since Phase 0) so
            // the NOT NULL Sessions.TeamId column below can default to its Id (1, the only row in
            // a freshly-created table). "WX0MIK" is the value already committed in appsettings.json's
            // ExamTools:Team today — not a secret. ExamToolsUsername/Password are deliberately left
            // NULL: migrations must never contain real credentials, even though this repo's existing
            // user-secrets already have working ones — see docs/multi-team.md for the required
            // manual follow-up (re-enter them on this row via direct DB edit) before ingestion works
            // again post-deploy.
            migrationBuilder.InsertData(
                table: "Teams",
                columns: ["Name", "ExamToolsTeamCode", "CreatedUtc"],
                values: ["WX0MIK", "WX0MIK", DateTime.UtcNow]);

            migrationBuilder.AddColumn<int>(
                name: "TeamId",
                table: "Sessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TeamId",
                table: "JobRunHistories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TeamId",
                table: "Sessions",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRunHistories_TeamId",
                table: "JobRunHistories",
                column: "TeamId");

            migrationBuilder.AddForeignKey(
                name: "FK_JobRunHistories_Teams_TeamId",
                table: "JobRunHistories",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Teams_TeamId",
                table: "Sessions",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobRunHistories_Teams_TeamId",
                table: "JobRunHistories");

            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Teams_TeamId",
                table: "Sessions");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_TeamId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_JobRunHistories_TeamId",
                table: "JobRunHistories");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "JobRunHistories");
        }
    }
}
