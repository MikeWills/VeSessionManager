using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeSessionManager.Core.Migrations
{
    /// <inheritdoc />
    public partial class UniqueInitialExamPaymentPerCandidate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-added before the index below: CreateIndex would throw on any database that
            // already contains duplicate InitialExam payments, and BOTH Web and Worker call
            // Database.Migrate() at startup — so a failure here is not "a migration didn't apply",
            // it is "the deployment will not boot". The duplicates are exactly what the index exists
            // to prevent (the Web-vs-Worker payment-generation race), so a database that ran the
            // buggy code may well have some.
            //
            // Only provably inert rows are removed: same candidate, same InitialExam reason, still
            // Unpaid (Status = 0), and never given a Square link or order id — i.e. a pure duplicate
            // that nobody could have paid and no candidate was ever sent. The oldest row per
            // candidate (MIN(Id)) is always the one kept.
            //
            // Deliberately NOT a blanket dedupe: if a duplicate was ever linked or paid, it is left
            // in place and the CreateIndex below fails loudly. That case means two live checkout
            // links existed for one candidate and money may have moved twice — a human has to look
            // at it, and silently deleting the evidence would be the wrong call.
            migrationBuilder.Sql(@"
                DELETE FROM ""Payments""
                WHERE ""Reason"" = 0
                  AND ""Status"" = 0
                  AND ""PaymentLinkUrl"" IS NULL
                  AND ""SquarePaymentReferenceId"" IS NULL
                  AND ""Id"" NOT IN (
                      SELECT MIN(""Id"") FROM ""Payments"" WHERE ""Reason"" = 0 GROUP BY ""CandidateId""
                  );");

            migrationBuilder.DropIndex(
                name: "IX_Payments_CandidateId",
                table: "Payments");

            // Filtered to Reason = 0 (InitialExam) — a Retest payment legitimately repeats for the
            // same candidate. Dropping the plain CandidateId index above is safe: this composite
            // leads with CandidateId, so FK lookups still use it.
            migrationBuilder.CreateIndex(
                name: "IX_Payments_CandidateId_Reason",
                table: "Payments",
                columns: new[] { "CandidateId", "Reason" },
                unique: true,
                filter: "\"Reason\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_CandidateId_Reason",
                table: "Payments");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CandidateId",
                table: "Payments",
                column: "CandidateId");
        }
    }
}
