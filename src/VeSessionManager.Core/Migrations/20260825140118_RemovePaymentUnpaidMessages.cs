using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Deletes every <c>MessageRule</c> on the <c>PaymentUnpaid</c> trigger (value 3) — no team can
    /// see, edit, or enable one after this, since <c>MessageTriggerDefinitions.All</c> no longer lists
    /// it and creation is refused for anything not in that list.
    ///
    /// <para>Mike, 2026-08-25: <i>"PaymentUnpaid is literally worthless. If they didn't pay the test
    /// session fee, they couldn't test and/or the VEC would not process it. Remove it."</i> Its
    /// condition — an FCC application entered for a candidate who never paid to test — cannot
    /// legitimately arise, so nothing seeded on it was ever going to fire for a real reason.</para>
    ///
    /// <para><b>History survives, deliberately.</b> This does not touch <c>MessageRuleRuns</c> — a run
    /// already sent is a record of something that actually happened, and <c>MessageRuleId</c> is
    /// already <c>SetNull</c> with the rule's name and trigger snapshotted onto the row for exactly
    /// this situation (the same design <c>MessagesOwnTheirContent</c> relied on). Deleting the rule
    /// orphans the row cleanly; it does not erase it.</para>
    ///
    /// <para>⚠️ <b>Down does not restore the deleted rows.</b> Their words are gone with the delete —
    /// there is nothing to reconstruct from. Rolling back past this only makes the trigger
    /// creatable/visible again; any team that wants the old notice back has to write it fresh.</para>
    /// </summary>
    public partial class RemovePaymentUnpaidMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DELETE FROM MessageRules WHERE Trigger = 3;");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
