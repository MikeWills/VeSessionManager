using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class Phase6_5bDiscordMultiTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<ulong>(
                name: "DiscordGuildId",
                table: "Teams",
                type: "INTEGER",
                nullable: true);

            // DiscordGuildId = the real MARC server id was already committed in appsettings.json's
            // Discord:GuildId — copy it over for the existing team so nothing regresses on deploy.
            // Discord:BotToken stays global/untouched (the bot is shared across every team).
            migrationBuilder.UpdateData(
                table: "Teams",
                keyColumn: "Id",
                keyValue: 1,
                column: "DiscordGuildId",
                value: 1323140214008578111UL);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscordGuildId",
                table: "Teams");
        }
    }
}
