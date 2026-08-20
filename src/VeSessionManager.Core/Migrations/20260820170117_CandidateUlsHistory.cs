using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class CandidateUlsHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandidateUlsHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CandidateId = table.Column<int>(type: "INTEGER", nullable: false),
                    LogDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CodeText = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateUlsHistoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CandidateUlsHistoryEntries_Candidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "Candidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateUlsHistoryEntries_CandidateId_LogDateUtc",
                table: "CandidateUlsHistoryEntries",
                columns: new[] { "CandidateId", "LogDateUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateUlsHistoryEntries");
        }
    }
}
