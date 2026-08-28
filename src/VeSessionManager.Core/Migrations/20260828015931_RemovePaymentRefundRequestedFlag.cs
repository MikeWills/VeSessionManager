using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class RemovePaymentRefundRequestedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_AspNetUsers_RefundRequestedByUserId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_RefundRequestedByUserId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundNotes",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundRequested",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundRequestedByUserId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RefundRequestedUtc",
                table: "Payments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefundNotes",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RefundRequested",
                table: "Payments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RefundRequestedByUserId",
                table: "Payments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundRequestedUtc",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_RefundRequestedByUserId",
                table: "Payments",
                column: "RefundRequestedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_AspNetUsers_RefundRequestedByUserId",
                table: "Payments",
                column: "RefundRequestedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
