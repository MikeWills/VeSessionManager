using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Backfills <c>MessageRuleRun</c> rows for the three sends that were only ever recorded in a
    /// <c>...SentUtc</c> column (#417), so a candidate's email history can read the run log alone and
    /// stop carrying per-column fallbacks (#415).
    ///
    /// <para><b>No schema change.</b> Data only, and the columns stay — nothing reads them afterwards,
    /// but leaving them means a rollback is a code revert rather than a migration. Dropping them is a
    /// separate change, once that has been confirmed.</para>
    ///
    /// <para><b>Every insert guards against a run that already covers the same send.</b> The felony
    /// column is written by both the on-demand button and <c>FelonyDisclosureDeclaredScanner</c>, so
    /// a rule-sent one already has a run; inserting again would list one email twice under two names.
    /// The same guard on the payment reminder keeps it from doubling up with what the #401 migration
    /// backfilled from <c>Candidate.FccFeeReminderSentUtc</c>.</para>
    ///
    /// <para>Labels are the literals on <c>CandidateNotificationService</c>. They have to match, or a
    /// send made before this migration and one made after appear under different names.</para>
    /// </summary>
    public partial class HandSendRunBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MessageTrigger.SentByHand — not a trigger point, a note that a person pressed a button.
            const int sentByHand = 100;
            const int felonyDisclosureDeclared = 6;
            const int fccFeeOutstanding = 2;

            // Youth Program instructions: no trigger can send these, so there is nothing to collide
            // with and no guard needed beyond the column being set.
            migrationBuilder.Sql($"""
                INSERT INTO MessageRuleRuns (TeamId, MessageRuleId, RuleName, Trigger, SubjectType, SubjectId, FiredUtc, Outcome, Detail)
                SELECT s.TeamId, NULL, 'Youth Program instructions', {sentByHand}, 0, c.Id,
                       c.YouthProgramInstructionsSentUtc, 0, 'Backfilled from Candidate.YouthProgramInstructionsSentUtc'
                FROM Candidates c
                JOIN Sessions s ON s.Id = c.SessionId
                WHERE c.YouthProgramInstructionsSentUtc IS NOT NULL;
                """);

            migrationBuilder.Sql($"""
                INSERT INTO MessageRuleRuns (TeamId, MessageRuleId, RuleName, Trigger, SubjectType, SubjectId, FiredUtc, Outcome, Detail)
                SELECT s.TeamId, NULL, 'Felony disclosure instructions', {felonyDisclosureDeclared}, 0, c.Id,
                       c.FelonyDisclosureInstructionsSentUtc, 0, 'Backfilled from Candidate.FelonyDisclosureInstructionsSentUtc'
                FROM Candidates c
                JOIN Sessions s ON s.Id = c.SessionId
                WHERE c.FelonyDisclosureInstructionsSentUtc IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM MessageRuleRuns r
                      WHERE r.SubjectType = 0 AND r.SubjectId = c.Id AND r.Trigger = {felonyDisclosureDeclared});
                """);

            // Payment.PaymentReminderSentUtc has had no writer since #401 moved the FCC fee reminder
            // onto a rule. Recorded against the candidate rather than the payment, because this list
            // answers "what did this person receive" — the mail went to them, whatever it was about.
            migrationBuilder.Sql($"""
                INSERT INTO MessageRuleRuns (TeamId, MessageRuleId, RuleName, Trigger, SubjectType, SubjectId, FiredUtc, Outcome, Detail)
                SELECT s.TeamId, NULL,
                       CASE WHEN p.Reason = 1 THEN 'Payment reminder email (retest)' ELSE 'Payment reminder email' END,
                       {fccFeeOutstanding}, 0, c.Id, p.PaymentReminderSentUtc, 0,
                       'Backfilled from Payment.PaymentReminderSentUtc'
                FROM Payments p
                JOIN Candidates c ON c.Id = p.CandidateId
                JOIN Sessions s ON s.Id = c.SessionId
                WHERE p.PaymentReminderSentUtc IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM MessageRuleRuns r
                      WHERE r.SubjectType = 0 AND r.SubjectId = c.Id AND r.Trigger = {fccFeeOutstanding});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Identifiable by their Detail, which is exactly why each insert above sets one.
            migrationBuilder.Sql("""
                DELETE FROM MessageRuleRuns
                WHERE Detail IN (
                    'Backfilled from Candidate.YouthProgramInstructionsSentUtc',
                    'Backfilled from Candidate.FelonyDisclosureInstructionsSentUtc',
                    'Backfilled from Payment.PaymentReminderSentUtc');
                """);
        }
    }
}
