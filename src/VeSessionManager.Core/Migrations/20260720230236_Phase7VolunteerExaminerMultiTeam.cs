using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase7VolunteerExaminerMultiTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill any existing VolunteerExaminer rows to the seeded team's id (1) — same
            // pattern as every other multi-team migration's defaultValue backfill. A default of 0
            // would violate the FK constraint added below, since no Team.Id = 0 row exists.
            migrationBuilder.AddColumn<int>(
                name: "TeamId",
                table: "VolunteerExaminers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerExaminers_TeamId_CallSign",
                table: "VolunteerExaminers",
                columns: new[] { "TeamId", "CallSign" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerExaminers_Teams_TeamId",
                table: "VolunteerExaminers",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerExaminers_Teams_TeamId",
                table: "VolunteerExaminers");

            migrationBuilder.DropIndex(
                name: "IX_VolunteerExaminers_TeamId_CallSign",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "VolunteerExaminers");
        }
    }
}
