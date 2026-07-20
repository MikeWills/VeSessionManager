using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase6_5cSquareMultiTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SquareAccessToken",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SquareLocationId",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SquareWebhookNotificationUrl",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SquareWebhookSignatureKey",
                table: "Teams",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SquareAccessToken",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "SquareLocationId",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "SquareWebhookNotificationUrl",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "SquareWebhookSignatureKey",
                table: "Teams");
        }
    }
}
