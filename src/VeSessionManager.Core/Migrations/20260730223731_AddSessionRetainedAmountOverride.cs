using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionRetainedAmountOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RetainedAmountOverride",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetainedAmountOverrideByUserId",
                table: "Sessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetainedAmountOverrideUtc",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_RetainedAmountOverrideByUserId",
                table: "Sessions",
                column: "RetainedAmountOverrideByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_AspNetUsers_RetainedAmountOverrideByUserId",
                table: "Sessions",
                column: "RetainedAmountOverrideByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_AspNetUsers_RetainedAmountOverrideByUserId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_RetainedAmountOverrideByUserId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RetainedAmountOverride",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RetainedAmountOverrideByUserId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RetainedAmountOverrideUtc",
                table: "Sessions");
        }
    }
}
