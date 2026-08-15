using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class PerTeamIntegrationSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DiscordEnabled",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailEnabled",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IntegrationOverridesEnabled",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SquareEnabled",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ZoomEnabled",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscordEnabled",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "EmailEnabled",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "IntegrationOverridesEnabled",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "SquareEnabled",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ZoomEnabled",
                table: "Teams");
        }
    }
}
