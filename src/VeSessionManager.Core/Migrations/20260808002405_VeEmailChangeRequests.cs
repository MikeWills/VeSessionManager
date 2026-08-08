using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class VeEmailChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VeEmailChangeRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolunteerExaminerId = table.Column<int>(type: "INTEGER", nullable: false),
                    NewEmail = table.Column<string>(type: "TEXT", nullable: false),
                    ConfirmationSentToEmail = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfirmedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VeEmailChangeRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VeEmailChangeRequests_VolunteerExaminers_VolunteerExaminerId",
                        column: x => x.VolunteerExaminerId,
                        principalTable: "VolunteerExaminers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VeEmailChangeRequests_TokenHash",
                table: "VeEmailChangeRequests",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VeEmailChangeRequests_VolunteerExaminerId",
                table: "VeEmailChangeRequests",
                column: "VolunteerExaminerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VeEmailChangeRequests");
        }
    }
}
