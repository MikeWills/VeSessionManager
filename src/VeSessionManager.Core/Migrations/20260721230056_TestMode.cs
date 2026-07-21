using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class TestMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TestModeEnabled",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TestModeOverrideEmail",
                table: "SystemSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TestModeEnabled",
                table: "SystemSettings");

            migrationBuilder.DropColumn(
                name: "TestModeOverrideEmail",
                table: "SystemSettings");
        }
    }
}
