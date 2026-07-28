using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase12YouthPaymentConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SquarePaymentLinkId",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "YouthConfirmationToken",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "YouthExamFeeAmount",
                table: "FeeConfigurations",
                type: "TEXT",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_YouthConfirmationToken",
                table: "Payments",
                column: "YouthConfirmationToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_YouthConfirmationToken",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "SquarePaymentLinkId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "YouthConfirmationToken",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "YouthExamFeeAmount",
                table: "FeeConfigurations");
        }
    }
}
