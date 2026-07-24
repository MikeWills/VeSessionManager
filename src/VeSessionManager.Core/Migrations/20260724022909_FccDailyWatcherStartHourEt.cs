using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class FccDailyWatcherStartHourEt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FccDailyWatcherStartHourEt",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FccDailyWatcherStartHourEt",
                table: "SystemSettings");
        }
    }
}
