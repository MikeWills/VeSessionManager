using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class MessageRuleDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MessageRuleRuns_MessageRules_MessageRuleId",
                table: "MessageRuleRuns");

            migrationBuilder.AddColumn<DateTime>(
                name: "MessageRulesSeededUtc",
                table: "Teams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MessageRuleId",
                table: "MessageRuleRuns",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddForeignKey(
                name: "FK_MessageRuleRuns_MessageRules_MessageRuleId",
                table: "MessageRuleRuns",
                column: "MessageRuleId",
                principalTable: "MessageRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Every team that already exists was seeded by the MessageRules migration, so it is marked
            // as done and will never be seeded again — which is what makes deleting a rule stick
            // rather than having it re-added on the next Worker start. Without this line the tombstone
            // is null everywhere and the first Worker start after deploy hands every team a second
            // full set of rules, duplicating the four it already has.
            migrationBuilder.Sql(
                "UPDATE Teams SET MessageRulesSeededUtc = strftime('%Y-%m-%d %H:%M:%f', 'now') WHERE MessageRulesSeededUtc IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MessageRuleRuns_MessageRules_MessageRuleId",
                table: "MessageRuleRuns");

            migrationBuilder.DropColumn(
                name: "MessageRulesSeededUtc",
                table: "Teams");

            migrationBuilder.AlterColumn<int>(
                name: "MessageRuleId",
                table: "MessageRuleRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MessageRuleRuns_MessageRules_MessageRuleId",
                table: "MessageRuleRuns",
                column: "MessageRuleId",
                principalTable: "MessageRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
