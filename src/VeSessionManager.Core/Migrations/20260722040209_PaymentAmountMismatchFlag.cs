using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class PaymentAmountMismatchFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AmountMismatchFlaggedUtc",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SquareAmountPaidUsd",
                table: "Payments",
                type: "TEXT",
                precision: 10,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountMismatchFlaggedUtc",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "SquareAmountPaidUsd",
                table: "Payments");
        }
    }
}
