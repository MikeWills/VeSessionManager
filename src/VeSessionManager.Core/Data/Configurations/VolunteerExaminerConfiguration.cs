using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="VolunteerExaminer"/>, split out of <c>AppDbContext.OnModelCreating</c>
/// (#311, S-04). The comments here came with the rules they explain — several record decisions that
/// cost real debugging, so they travel with the configuration rather than staying behind in a file
/// that no longer holds it.
/// </summary>
public class VolunteerExaminerConfiguration : IEntityTypeConfiguration<VolunteerExaminer>
{
    public void Configure(EntityTypeBuilder<VolunteerExaminer> b)
    {
        // FRN is unique where present — it is the stable identity, and two people cannot share
        // one. Filtered, because almost every row has none: ExamTools never reports an FRN, so
        // it only arrives once the ULS sweep backfills it. SQLite treats NULLs as distinct in a
        // unique index anyway; the filter states the intent for a reader.
        b.HasIndex(v => v.Frn).IsUnique().HasFilter("\"Frn\" IS NOT NULL");

        // Email is unique where present, and the four code paths that already enforced that
        // (VolunteerExaminerManagementService x2, VeEmailChangeService x2) were backed by nothing
        // — two concurrent requests both passed the check and both committed (#284). What made it
        // more than untidy: VeSelfServiceLinkService then resolved an address with
        // FirstOrDefaultAsync, so a sign-in link — a bearer credential reaching personal data —
        // went to whichever duplicate SQLite returned.
        //
        // NOCASE, because those four checks compare `Email.ToLower() == ...`. A default index is
        // case-SENSITIVE in SQLite, so it would have allowed "A@x.com" beside "a@x.com" — a
        // guard weaker than the rule it is supposed to be enforcing, which is worse than none
        // because it reads as settled.
        b.Property(v => v.Email).UseCollation("NOCASE");
        b.HasIndex(v => v.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");

        // **CallSign is deliberately NOT unique**, though only one person holds a given call at
        // any moment. Two reasons, both practical rather than theoretical:
        //
        //   1. The identity signal is weaker than the identity concept. Merging the old
        //      per-team rows can only match on call sign, and a call sign released and reissued
        //      to a *different* person would merge two humans irreversibly. The migration
        //      therefore merges only when the name agrees too and leaves the rest alone — which
        //      a unique index would reject outright, turning a data-quality question into a
        //      migration that cannot run.
        //   2. Those survivors are surfaced as "possible duplicates" for an admin to resolve
        //      (phase 2). A constraint cannot express "probably the same person, ask someone".
        //
        // Uniqueness is enforced where it is actually knowable: on Frn above.
        b.HasIndex(v => v.CallSign);

        b.HasOne(v => v.MergedIntoVolunteerExaminer)
            .WithMany()
            .HasForeignKey(v => v.MergedIntoVolunteerExaminerId)
            .OnDelete(DeleteBehavior.Restrict);

        // **A merged duplicate disappears from every query at once.** The alternative — asking
        // each query to remember to exclude them — is an invariant that one future query will
        // forget, and the symptom would be a person appearing twice on a screen long after
        // someone merged them. Merged rows are still reachable via IgnoreQueryFilters() for the
        // audit trail and for an un-merge.
        b.HasQueryFilter(v => v.MergedIntoVolunteerExaminerId == null);
        
    }
}
