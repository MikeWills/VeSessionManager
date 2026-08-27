using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCoppaAndYouthConfirmIntro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "YouthConfirmIntroHtml",
                table: "EmailSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CoppaFormSentConfirmedUtc",
                table: "Candidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeclaredUnder13",
                table: "Candidates",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "YouthConfirmIntroHtml",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "CoppaFormSentConfirmedUtc",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "DeclaredUnder13",
                table: "Candidates");
        }
    }
}
