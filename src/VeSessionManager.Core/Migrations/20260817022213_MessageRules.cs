using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class MessageRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    ParameterHours = table.Column<int>(type: "INTEGER", nullable: true),
                    TemplateKey = table.Column<string>(type: "TEXT", nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    Recipient = table.Column<int>(type: "INTEGER", nullable: false),
                    FanOut = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageRules_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MessageRuleRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageRuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    RuleName = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    SubjectType = table.Column<int>(type: "INTEGER", nullable: false),
                    SubjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    FiredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageRuleRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageRuleRuns_MessageRules_MessageRuleId",
                        column: x => x.MessageRuleId,
                        principalTable: "MessageRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MessageRuleRuns_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageRuleRuns_MessageRuleId_SubjectId",
                table: "MessageRuleRuns",
                columns: new[] { "MessageRuleId", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageRuleRuns_TeamId_FiredUtc",
                table: "MessageRuleRuns",
                columns: new[] { "TeamId", "FiredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageRules_TeamId_Trigger_IsEnabled",
                table: "MessageRules",
                columns: new[] { "TeamId", "Trigger", "IsEnabled" });

            SeedRulesForExistingTeams(migrationBuilder);
            BackfillRunsFromLegacyColumns(migrationBuilder);
        }

        /// <summary>
        /// Gives every team that already exists the four rules reproducing what this app sent
        /// automatically before trigger points (#401), so nothing a candidate receives changes on
        /// deploy.
        ///
        /// <para><b>Why here as well as in EmailDefaultsSeeder.</b> The seeder runs at Worker startup,
        /// which is after migrations — so the backfill below would have no rules to attach its markers
        /// to. Both are idempotent per (team, trigger): whichever runs first wins, and the other skips.</para>
        ///
        /// <para><c>CreatedUtc</c> is the moment of the migration, and it is load-bearing. Every scan
        /// is bounded by it, so a rule created now cannot fire for a subject whose trigger moment has
        /// already passed — which is what stops a deployment mailing everybody who is currently five
        /// days into an outstanding FCC fee.</para>
        /// </summary>
        private static void SeedRulesForExistingTeams(MigrationBuilder migrationBuilder)
        {
            // strftime rather than a C# timestamp baked into the file: a migration written today may
            // first run on a server months from now, and the bound that matters is "when did this
            // deployment get the rules", not "when was this file authored".
            const string now = "strftime('%Y-%m-%d %H:%M:%f', 'now')";

            // Trigger/Channel/Recipient/FanOut are the pinned integer values of MessageTrigger,
            // MessageChannel, MessageRecipient and MessageFanOut. Written as numbers because SQL has
            // no access to the enums; the pinning rule in Entities/Enums.cs is what makes that safe.
            Insert(0, "Registration confirmation", "RegistrationConfirmation", "NULL", recipient: 0);
            Insert(1, "Reminder 24 hours before the session", "DayBeforeReminder", "24", recipient: 0);
            Insert(2, "FCC fee reminder after 5 days", "FccFeeReminder5Day", "120", recipient: 0);
            // Recipient 1 = TeamAdminAddress. The one message here that never went to a candidate.
            Insert(3, "Unpaid payment notice after 10 days", "PaymentExpirationNotice", "240", recipient: 1);

            void Insert(int trigger, string name, string templateKey, string parameterHours, int recipient) =>
                migrationBuilder.Sql($"""
                    INSERT INTO MessageRules (TeamId, Name, Trigger, ParameterHours, TemplateKey, Channel, Recipient, FanOut, IsEnabled, CreatedUtc)
                    SELECT t.Id, '{name}', {trigger}, {parameterHours}, '{templateKey}', 0, {recipient}, 0, 1, {now}
                    FROM Teams t
                    WHERE NOT EXISTS (SELECT 1 FROM MessageRules r WHERE r.TeamId = t.Id AND r.Trigger = {trigger});
                    """);
        }

        /// <summary>
        /// Writes one <c>MessageRuleRun</c> marker per message this app has <b>already</b> sent, so the
        /// first tick after deploy has nothing to catch up on.
        ///
        /// <para>Belt and braces with the <c>CreatedUtc</c> bound above, deliberately. Either alone
        /// prevents a re-send; two independent guards is the answer to the failure Mike named on the
        /// issue — "nothing worse than sending out 3000 emails because you added a new rule".</para>
        ///
        /// <para>Raw SQL, which means <b>invisible to EF InMemory</b> and to the compiler both: a
        /// backfill that resolves nothing looks exactly like one with nothing to do. Driven against
        /// real SQLite in <c>MessageRuleBackfillSqliteTests</c>, the same way the AuditLog.TeamId
        /// backfill was (docs/audit-log.md).</para>
        ///
        /// <para>The unpaid-payment marker comes from <c>Payment.ExpiredUnpaid</c> rather than a
        /// timestamp column, because that notice never had one — the flag <i>was</i> its idempotency
        /// guard. So its FiredUtc is the rule's creation rather than the real send time, and the Detail
        /// column says so rather than leaving a plausible-looking wrong timestamp.</para>
        /// </summary>
        private static void BackfillRunsFromLegacyColumns(MigrationBuilder migrationBuilder)
        {
            // Outcome 0 = Sent, SubjectType 0 = Candidate / 1 = Payment.
            FromCandidateColumn(0, "RegistrationConfirmationSentUtc");
            FromCandidateColumn(1, "DayBeforeReminderSentUtc");
            FromCandidateColumn(2, "FccFeeReminderSentUtc");

            migrationBuilder.Sql("""
                INSERT INTO MessageRuleRuns (TeamId, MessageRuleId, RuleName, Trigger, SubjectType, SubjectId, FiredUtc, Outcome, Detail)
                SELECT r.TeamId, r.Id, r.Name, 3, 1, p.Id, r.CreatedUtc, 0,
                       'Backfilled from Payment.ExpiredUnpaid; the original notice had no recorded send time'
                FROM Payments p
                JOIN Candidates c ON c.Id = p.CandidateId
                JOIN Sessions s ON s.Id = c.SessionId
                JOIN MessageRules r ON r.TeamId = s.TeamId AND r.Trigger = 3
                WHERE p.ExpiredUnpaid = 1;
                """);

            void FromCandidateColumn(int trigger, string column) =>
                migrationBuilder.Sql($"""
                    INSERT INTO MessageRuleRuns (TeamId, MessageRuleId, RuleName, Trigger, SubjectType, SubjectId, FiredUtc, Outcome, Detail)
                    SELECT r.TeamId, r.Id, r.Name, {trigger}, 0, c.Id, c.{column}, 0, 'Backfilled from Candidate.{column}'
                    FROM Candidates c
                    JOIN Sessions s ON s.Id = c.SessionId
                    JOIN MessageRules r ON r.TeamId = s.TeamId AND r.Trigger = {trigger}
                    WHERE c.{column} IS NOT NULL;
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageRuleRuns");

            migrationBuilder.DropTable(
                name: "MessageRules");
        }
    }
}
