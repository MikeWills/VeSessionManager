using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class VeContactRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PiiPurgedUtc",
                table: "VolunteerExaminers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VeContactRetentionYears",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PiiPurgedUtc",
                table: "VolunteerExaminers");

            migrationBuilder.DropColumn(
                name: "VeContactRetentionYears",
                table: "SystemSettings");
        }
    }
}
