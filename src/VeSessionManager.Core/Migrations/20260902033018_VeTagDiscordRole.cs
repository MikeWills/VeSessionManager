using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class VeTagDiscordRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "DiscordRoleId",
                table: "VeTags",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordRoleName",
                table: "VeTags",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VeTags_TeamId_DiscordRoleId",
                table: "VeTags",
                columns: new[] { "TeamId", "DiscordRoleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VeTags_TeamId_DiscordRoleId",
                table: "VeTags");

            migrationBuilder.DropColumn(
                name: "DiscordRoleId",
                table: "VeTags");

            migrationBuilder.DropColumn(
                name: "DiscordRoleName",
                table: "VeTags");
        }
    }
}
