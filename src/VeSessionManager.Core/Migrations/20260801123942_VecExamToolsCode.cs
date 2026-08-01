using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class VecExamToolsCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExamToolsCode",
                table: "Vecs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vecs_ExamToolsCode",
                table: "Vecs",
                column: "ExamToolsCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Vecs_ExamToolsCode",
                table: "Vecs");

            migrationBuilder.DropColumn(
                name: "ExamToolsCode",
                table: "Vecs");
        }
    }
}
