using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <summary>
    /// Repairs candidates left with <c>Tested = 0</c> by the old <c>MarkFailedAsync</c>, which set
    /// <c>ApplicationStatus = Failed</c> without setting <c>Tested</c> (fixed 2026-08-15). Someone who
    /// failed an exam sat one, so the two fields disagreed — and the disagreement left them eligible
    /// for the no-show delete, which is gated on <c>!Tested</c> and nulls PII immediately.
    ///
    /// <para>The predicate is exact rather than approximate. <c>ResultMarkedByUserId IS NOT NULL</c>
    /// is set only by the manual button; <c>ExamResultSyncService</c>'s auto-fail leaves it null and
    /// always set <c>Tested</c> correctly, so this cannot touch an auto-failed row. On this
    /// deployment it matches zero rows — the manual button had never been used on real data — but
    /// this repo is public and self-hosted elsewhere, where it may not be zero.</para>
    /// </summary>
    public partial class BackfillTestedOnManuallyFailedCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ApplicationStatus 3 == CandidateApplicationStatus.Failed. Written as the integer
            // because a migration is a historical record: it must keep meaning what it meant when it
            // ran, even if the enum is renamed or reordered later.
            migrationBuilder.Sql("""
                UPDATE "Candidates"
                SET "Tested" = 1
                WHERE "ApplicationStatus" = 3
                  AND "ResultMarkedByUserId" IS NOT NULL
                  AND "Tested" = 0;
                """);
        }

        /// <summary>
        /// Deliberately a no-op.
        /// </summary>
        /// <remarks>
        /// Reverting would mean clearing <c>Tested</c> on manually-failed candidates, and there is no
        /// way to tell a row this migration changed from one that was already correct — an exam
        /// failure recorded after this shipped looks identical. Clearing both would re-open the
        /// no-show delete on people who really did test, which is the data-loss path this migration
        /// exists to close.
        ///
        /// <para>Rolling the code back is safe with this applied: the old <c>MarkFailedAsync</c>
        /// simply would not set <c>Tested</c> on future rows, and nothing reads it in a way that a
        /// correctly-set value breaks. Per the repo's rollback policy, the documented path for
        /// undoing this is the pre-migration backup, not a down-migration.</para>
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
