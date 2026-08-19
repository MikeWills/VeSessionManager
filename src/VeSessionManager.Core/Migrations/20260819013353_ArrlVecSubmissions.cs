using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class ArrlVecSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArrlVecSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: true),
                    SubmittedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    SubmittedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: false),
                    CallSign = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    SessionDate = table.Column<string>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: false),
                    PaymentMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountCharged = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ArchiveFileName = table.Column<string>(type: "TEXT", nullable: false),
                    ArchiveStoredPath = table.Column<string>(type: "TEXT", nullable: true),
                    ArchiveByteCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AttachmentFileName = table.Column<string>(type: "TEXT", nullable: true),
                    AttachmentStoredPath = table.Column<string>(type: "TEXT", nullable: true),
                    AttachmentByteCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    UnconfirmedFileNames = table.Column<string>(type: "TEXT", nullable: true),
                    TransportError = table.Column<string>(type: "TEXT", nullable: true),
                    FilesPurgedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrlVecSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArrlVecSubmissions_AspNetUsers_SubmittedByUserId",
                        column: x => x.SubmittedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArrlVecSubmissions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ArrlVecSubmissions_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArrlVecSubmissions_SessionId",
                table: "ArrlVecSubmissions",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ArrlVecSubmissions_SubmittedByUserId",
                table: "ArrlVecSubmissions",
                column: "SubmittedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ArrlVecSubmissions_TeamId",
                table: "ArrlVecSubmissions",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArrlVecSubmissions");
        }
    }
}
