using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class ArrlSubmissionArchiveRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VecSubmissionArchiveRetentionDays",
                table: "SystemSettings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VecSubmissionArchiveRetentionDays",
                table: "SystemSettings");
        }
    }
}
