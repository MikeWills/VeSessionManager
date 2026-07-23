using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class CandidateEmailSentTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FelonyDisclosureInstructionsSentUtc",
                table: "Candidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "YouthProgramInstructionsSentUtc",
                table: "Candidates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FelonyDisclosureInstructionsSentUtc",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "YouthProgramInstructionsSentUtc",
                table: "Candidates");
        }
    }
}
