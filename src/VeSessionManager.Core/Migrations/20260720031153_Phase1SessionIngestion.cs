using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase1SessionIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Candidates_SessionId",
                table: "Candidates");

            migrationBuilder.AddColumn<string>(
                name: "ExamToolsApplicantId",
                table: "Candidates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ExamToolsSessionId",
                table: "Sessions",
                column: "ExamToolsSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SessionId_ExamToolsApplicantId",
                table: "Candidates",
                columns: new[] { "SessionId", "ExamToolsApplicantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_ExamToolsSessionId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Candidates_SessionId_ExamToolsApplicantId",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "ExamToolsApplicantId",
                table: "Candidates");

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SessionId",
                table: "Candidates",
                column: "SessionId");
        }
    }
}
