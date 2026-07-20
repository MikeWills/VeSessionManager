using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase8VecSubmissionRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Users_ArrlSubmittedByUserId",
                table: "Sessions");

            migrationBuilder.RenameColumn(
                name: "ArrlSubmittedDate",
                table: "Sessions",
                newName: "VecSubmittedDate");

            migrationBuilder.RenameColumn(
                name: "ArrlSubmittedByUserId",
                table: "Sessions",
                newName: "VecSubmittedByUserId");

            migrationBuilder.RenameColumn(
                name: "ArrlSubmissionStatus",
                table: "Sessions",
                newName: "VecSubmissionStatus");

            migrationBuilder.RenameIndex(
                name: "IX_Sessions_ArrlSubmittedByUserId",
                table: "Sessions",
                newName: "IX_Sessions_VecSubmittedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Users_VecSubmittedByUserId",
                table: "Sessions",
                column: "VecSubmittedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sessions_Users_VecSubmittedByUserId",
                table: "Sessions");

            migrationBuilder.RenameColumn(
                name: "VecSubmittedDate",
                table: "Sessions",
                newName: "ArrlSubmittedDate");

            migrationBuilder.RenameColumn(
                name: "VecSubmittedByUserId",
                table: "Sessions",
                newName: "ArrlSubmittedByUserId");

            migrationBuilder.RenameColumn(
                name: "VecSubmissionStatus",
                table: "Sessions",
                newName: "ArrlSubmissionStatus");

            migrationBuilder.RenameIndex(
                name: "IX_Sessions_VecSubmittedByUserId",
                table: "Sessions",
                newName: "IX_Sessions_ArrlSubmittedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sessions_Users_ArrlSubmittedByUserId",
                table: "Sessions",
                column: "ArrlSubmittedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
