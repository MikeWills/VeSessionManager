using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Lets the seeder run again for any team the content refactor left with no messages at all.
    ///
    /// <para><c>MessagesOwnTheirContent</c> deleted every <c>MessageRule</c> — deliberately, since a
    /// rule's words lived in a table that was going away — but left <c>Team.MessageRulesSeededUtc</c>
    /// stamped. That field is a tombstone meaning "this team has been set up, never seed it again",
    /// and it is load-bearing: without it, a message somebody deleted comes back on the next Worker
    /// start, quietly resuming a send they had stopped (#401 PR2).</para>
    ///
    /// <para>The combination is what nobody noticed. Every existing team ended up with the rules
    /// deleted <i>and</i> the tombstone saying not to re-seed, so it would have had <b>no messages at
    /// all, permanently</b> — while a brand-new team got the seven examples. The two per-candidate
    /// buttons would silently do nothing, and the Messages page would read as broken rather than as a
    /// fresh start.</para>
    ///
    /// <para><b>Scoped to teams with no messages, not all of them.</b> A team created in the window
    /// between that migration and this one already has its seven, and clearing its tombstone would
    /// give it fourteen. The window is small enough to be theoretical and the predicate is cheap
    /// enough not to care.</para>
    /// </summary>
    public partial class ReseedMessagesForTeamsLeftWithNone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                UPDATE Teams
                SET MessageRulesSeededUtc = NULL
                WHERE Id NOT IN (SELECT DISTINCT TeamId FROM MessageRules);
                """);

        /// <summary>
        /// Nothing. Re-stamping the tombstone would claim these teams had been set up when the
        /// seeder may not have run yet, which is the state this migration exists to get out of.
        /// Rolling back past this leaves the seeder free to run, which is harmless — it adds nothing
        /// to a team that already has messages.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
