using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase6_5aZoomMultiTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ZoomAccountId",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZoomClientId",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZoomClientSecret",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZoomUserId",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            // ZoomUserId = "me" was already the effective value for the seeded team (the old
            // ZoomOptions.UserId default) — copy it over so nothing regresses on deploy. Only
            // this specific row, not a column-level default: future teams should stay NULL unless
            // explicitly set (ZoomClient falls back to "me" in code, not via a stored DB default).
            migrationBuilder.UpdateData(
                table: "Teams",
                keyColumn: "Id",
                keyValue: 1,
                column: "ZoomUserId",
                value: "me");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ZoomAccountId",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ZoomClientId",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ZoomClientSecret",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "ZoomUserId",
                table: "Teams");
        }
    }
}
