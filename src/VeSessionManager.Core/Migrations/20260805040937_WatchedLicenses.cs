using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class WatchedLicenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WatchedLicenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    CallSign = table.Column<string>(type: "TEXT", nullable: false),
                    Frn = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    AddedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotFoundAtFcc = table.Column<bool>(type: "INTEGER", nullable: false),
                    LicenseeName = table.Column<string>(type: "TEXT", nullable: true),
                    LicenseStatus = table.Column<string>(type: "TEXT", nullable: true),
                    OperatorClass = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiredDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancellationDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RenewalPendingSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RenewalFileNumber = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiredDateWhenRenewalFiledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RenewalConfirmedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedLicenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchedLicenses_AspNetUsers_AddedByUserId",
                        column: x => x.AddedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WatchedLicenses_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchedLicenses_AddedByUserId",
                table: "WatchedLicenses",
                column: "AddedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchedLicenses_LastCheckedUtc",
                table: "WatchedLicenses",
                column: "LastCheckedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WatchedLicenses_TeamId_CallSign",
                table: "WatchedLicenses",
                columns: new[] { "TeamId", "CallSign" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WatchedLicenses");
        }
    }
}
