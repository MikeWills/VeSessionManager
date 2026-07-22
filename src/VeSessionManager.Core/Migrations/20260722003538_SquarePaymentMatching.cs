using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class SquarePaymentMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SquareOrderCompletedUtc",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UnmatchedSquarePayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    SquareOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    SquarePaymentId = table.Column<string>(type: "TEXT", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: false),
                    BuyerEmailAddress = table.Column<string>(type: "TEXT", nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchedPaymentId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnmatchedSquarePayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnmatchedSquarePayments_AspNetUsers_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UnmatchedSquarePayments_Payments_MatchedPaymentId",
                        column: x => x.MatchedPaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UnmatchedSquarePayments_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnmatchedSquarePayments_MatchedPaymentId",
                table: "UnmatchedSquarePayments",
                column: "MatchedPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_UnmatchedSquarePayments_ResolvedByUserId",
                table: "UnmatchedSquarePayments",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UnmatchedSquarePayments_TeamId_SquareOrderId",
                table: "UnmatchedSquarePayments",
                columns: new[] { "TeamId", "SquareOrderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnmatchedSquarePayments");

            migrationBuilder.DropColumn(
                name: "SquareOrderCompletedUtc",
                table: "Payments");
        }
    }
}
