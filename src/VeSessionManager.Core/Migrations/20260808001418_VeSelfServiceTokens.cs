using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class VeSelfServiceTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VeSelfServiceTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolunteerExaminerId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentToEmail = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VeSelfServiceTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VeSelfServiceTokens_VolunteerExaminers_VolunteerExaminerId",
                        column: x => x.VolunteerExaminerId,
                        principalTable: "VolunteerExaminers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VeSelfServiceTokens_TokenHash",
                table: "VeSelfServiceTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VeSelfServiceTokens_VolunteerExaminerId",
                table: "VeSelfServiceTokens",
                column: "VolunteerExaminerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VeSelfServiceTokens");
        }
    }
}
