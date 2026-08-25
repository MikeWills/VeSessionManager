using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Adds <c>Team.EmailConfiguredUtc</c> and <c>MessageRule.EnabledSinceUtc</c> — the two
    /// additional inputs to <c>MessageRuleEligibility.FloorUtc</c> (2026-08-25), so a rule switched
    /// back on or a team's email configured for the first time doesn't retroactively chase whoever
    /// became eligible while it was off. Mike: "it's not supposed to send any backlog of email" /
    /// "if a message is off then I turn on, it's not supposed to send backlog either."
    ///
    /// <para>Deliberately no backfill for either column. An already-configured team or an
    /// already-enabled rule has had no off-to-on transition for this migration to record — stamping
    /// migration time would invent one, wrongly hiding candidates already mid-cycle who registered
    /// days ago. Null means "no extra floor from this," which is exactly right for existing rows.</para>
    /// </summary>
    public partial class MessageEligibilityFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailConfiguredUtc",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EnabledSinceUtc",
                table: "MessageRules",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailConfiguredUtc",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "EnabledSinceUtc",
                table: "MessageRules");
        }
    }
}
