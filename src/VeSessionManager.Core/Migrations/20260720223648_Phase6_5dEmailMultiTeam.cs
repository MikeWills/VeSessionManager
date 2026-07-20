using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase6_5dEmailMultiTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailTemplates_Key",
                table: "EmailTemplates");

            migrationBuilder.AddColumn<string>(
                name: "SmtpHost",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpPassword",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SmtpPort",
                table: "Teams",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SmtpUseStartTls",
                table: "Teams",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpUsername",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            // Backfill existing EmailTemplate/EmailSettings rows to the seeded team's id (1, the
            // only Team row so far — same safe assumption as Sessions.TeamId in the ExamTools
            // slice), not 0 — a TeamId of 0 would violate the FK constraint added below, since no
            // Team.Id = 0 row exists.
            migrationBuilder.AddColumn<int>(
                name: "TeamId",
                table: "EmailTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TeamId",
                table: "EmailSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_TeamId_Key",
                table: "EmailTemplates",
                columns: new[] { "TeamId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSettings_TeamId",
                table: "EmailSettings",
                column: "TeamId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_EmailSettings_Teams_TeamId",
                table: "EmailSettings",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EmailTemplates_Teams_TeamId",
                table: "EmailTemplates",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmailSettings_Teams_TeamId",
                table: "EmailSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_EmailTemplates_Teams_TeamId",
                table: "EmailTemplates");

            migrationBuilder.DropIndex(
                name: "IX_EmailTemplates_TeamId_Key",
                table: "EmailTemplates");

            migrationBuilder.DropIndex(
                name: "IX_EmailSettings_TeamId",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "SmtpHost",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "SmtpPassword",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "SmtpPort",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "SmtpUseStartTls",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "SmtpUsername",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "EmailTemplates");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "EmailSettings");

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_Key",
                table: "EmailTemplates",
                column: "Key",
                unique: true);
        }
    }
}
