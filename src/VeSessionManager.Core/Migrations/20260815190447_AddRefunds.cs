using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SquarePaymentId",
                table: "Payments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Refunds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    UnmatchedSquarePaymentId = table.Column<int>(type: "INTEGER", nullable: true),
                    SquarePaymentId = table.Column<string>(type: "TEXT", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    SquareIdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    SquareRefundId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureDetail = table.Column<string>(type: "TEXT", nullable: true),
                    RequestedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SettledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.CheckConstraint("CK_Refund_ExactlyOneSource", "(\"PaymentId\" IS NULL) <> (\"UnmatchedSquarePaymentId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Refunds_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_UnmatchedSquarePayments_UnmatchedSquarePaymentId",
                        column: x => x.UnmatchedSquarePaymentId,
                        principalTable: "UnmatchedSquarePayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId",
                table: "Refunds",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_RequestedByUserId",
                table: "Refunds",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_SettledUtc",
                table: "Refunds",
                column: "SettledUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_SquareIdempotencyKey",
                table: "Refunds",
                column: "SquareIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_SquarePaymentId",
                table: "Refunds",
                column: "SquarePaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_TeamId",
                table: "Refunds",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_UnmatchedSquarePaymentId",
                table: "Refunds",
                column: "UnmatchedSquarePaymentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Refunds");

            migrationBuilder.DropColumn(
                name: "SquarePaymentId",
                table: "Payments");
        }
    }
}
