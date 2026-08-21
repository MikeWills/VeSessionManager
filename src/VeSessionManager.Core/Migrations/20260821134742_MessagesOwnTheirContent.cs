using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class MessagesOwnTheirContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ Existing rules are deleted, not migrated. Their TemplateKey named a template whose
            // words live in another table, so renaming the column would leave every rule with a key
            // like "DayBeforeReminder" as its subject line and an empty body — nonsense that looks
            // like data. Mike, 2026-08-21: "I have no emails that are important and I have no problems
            // losing [them] ... delete it all and re-create it all."
            //
            // MessageRuleRun.MessageRuleId is SetNull and the row snapshots the rule name and trigger,
            // so what was already sent survives this — the history outlives the rules that made it.
            migrationBuilder.Sql("DELETE FROM MessageRules;");

            migrationBuilder.RenameColumn(
                name: "TemplateKey",
                table: "MessageRules",
                newName: "Subject");

            migrationBuilder.AddColumn<string>(
                name: "Body",
                table: "MessageRules",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Body",
                table: "MessageRules");

            migrationBuilder.RenameColumn(
                name: "Subject",
                table: "MessageRules",
                newName: "TemplateKey");
        }
    }
}
