using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class ArrlSubmissionTeamConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArrlSubmissionEmail",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArrlSubmissionEmailSource",
                table: "Teams",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArrlSubmissionLocation",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArrlSubmissionNamePostfix",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArrlSubmissionNote",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArrlSubmissionPaymentMethod",
                table: "Teams",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArrlSubmissionEmail",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ArrlSubmissionEmailSource",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ArrlSubmissionLocation",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ArrlSubmissionNamePostfix",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ArrlSubmissionNote",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ArrlSubmissionPaymentMethod",
                table: "Teams");
        }
    }
}
