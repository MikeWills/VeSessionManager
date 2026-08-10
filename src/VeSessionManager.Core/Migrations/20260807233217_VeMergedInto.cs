using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class VeMergedInto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MergedIntoVolunteerExaminerId",
                table: "VolunteerExaminers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerExaminers_MergedIntoVolunteerExaminerId",
                table: "VolunteerExaminers",
                column: "MergedIntoVolunteerExaminerId");

            migrationBuilder.AddForeignKey(
                name: "FK_VolunteerExaminers_VolunteerExaminers_MergedIntoVolunteerExaminerId",
                table: "VolunteerExaminers",
                column: "MergedIntoVolunteerExaminerId",
                principalTable: "VolunteerExaminers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VolunteerExaminers_VolunteerExaminers_MergedIntoVolunteerExaminerId",
                table: "VolunteerExaminers");

            migrationBuilder.DropIndex(
                name: "IX_VolunteerExaminers_MergedIntoVolunteerExaminerId",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "MergedIntoVolunteerExaminerId",
                table: "VolunteerExaminers");
        }
    }
}
