using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class UserVolunteerExaminerLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VolunteerExaminerId",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_VolunteerExaminerId",
                table: "AspNetUsers",
                column: "VolunteerExaminerId",
                unique: true,
                filter: "\"VolunteerExaminerId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_VolunteerExaminers_VolunteerExaminerId",
                table: "AspNetUsers",
                column: "VolunteerExaminerId",
                principalTable: "VolunteerExaminers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_VolunteerExaminers_VolunteerExaminerId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_VolunteerExaminerId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "VolunteerExaminerId",
                table: "AspNetUsers");
        }
    }
}
