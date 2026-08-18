using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class MessageRuleEnvelope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BccAddress",
                table: "MessageRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CcAddress",
                table: "MessageRules",
                type: "TEXT",
                nullable: true);

            // true, matching the entity's own default — EF scaffolds a bool column as false and that
            // would quietly give every existing rule the opposite of what a rule created through the
            // app gets. Harmless today, since no existing rule has a Cc or Bcc to multiply, and
            // exactly the kind of divergence that is impossible to spot later.
            migrationBuilder.AddColumn<bool>(
                name: "MonitoringCopyOncePerRun",
                table: "MessageRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyToOverride",
                table: "MessageRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplyToSource",
                table: "MessageRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BccAddress",
                table: "MessageRules");

            migrationBuilder.DropColumn(
                name: "CcAddress",
                table: "MessageRules");

            migrationBuilder.DropColumn(
                name: "MonitoringCopyOncePerRun",
                table: "MessageRules");

            migrationBuilder.DropColumn(
                name: "ReplyToOverride",
                table: "MessageRules");

            migrationBuilder.DropColumn(
                name: "ReplyToSource",
                table: "MessageRules");
        }
    }
}
