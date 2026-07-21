using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase9cSystemSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PiiRetentionWindowDays = table.Column<int>(type: "INTEGER", nullable: true),
                    FccDailyWatcherIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    FccWeeklyCatchupIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    FccWeeklyCatchupDayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SystemSettings_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Singleton settings row. PiiRetentionWindowDays stays NULL — spec.md is explicit that
            // no default is assumed, an admin must set it before Phase 10's (not yet built) purge
            // job can run. FccXxx values carry over the current appsettings.json Jobs:* defaults.
            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: ["Id", "FccDailyWatcherIntervalHours", "FccWeeklyCatchupIntervalHours", "FccWeeklyCatchupDayOfWeek"],
                values: [1, 24, 24, (int)DayOfWeek.Monday]);

            migrationBuilder.CreateIndex(
                name: "IX_Vecs_Name",
                table: "Vecs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_Name",
                table: "Teams",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_UpdatedByUserId",
                table: "SystemSettings",
                column: "UpdatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropIndex(
                name: "IX_Vecs_Name",
                table: "Vecs");

            migrationBuilder.DropIndex(
                name: "IX_Teams_Name",
                table: "Teams");
        }
    }
}
