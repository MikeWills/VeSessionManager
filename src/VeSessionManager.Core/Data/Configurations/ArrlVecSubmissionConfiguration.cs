using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="ArrlVecSubmission"/> (#197).
///
/// <para><b>Nothing may cascade-delete this row.</b> It is the record that a session was filed with a
/// VEC — the thing Mike has had to go back to after the fact, and the only evidence of what was sent
/// when an outcome could not be confirmed. EF's default for a required relationship is
/// <c>Cascade</c>, which would have quietly destroyed a filing record along with the session it
/// describes, and a deleted session is precisely when someone might need to prove what went.</para>
///
/// <para>So every foreign key is nullable with <c>SetNull</c>: the row outlives the session, the team
/// and the user, keeping its own snapshot of what was submitted. Same shape as
/// <c>MessageRuleRun.MessageRuleId</c>, where the record of what was sent has to outlive the rule
/// that sent it.</para>
/// </summary>
public class ArrlVecSubmissionConfiguration : IEntityTypeConfiguration<ArrlVecSubmission>
{
    public void Configure(EntityTypeBuilder<ArrlVecSubmission> b)
    {
        b.HasOne(s => s.Session)
            .WithMany()
            .HasForeignKey(s => s.SessionId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(s => s.Team)
            .WithMany()
            .HasForeignKey(s => s.TeamId)
            .OnDelete(DeleteBehavior.SetNull);

        // The user is the exception, and is Restrict with a delete blocker instead — matching
        // Session.VecSubmittedByUserId, which already refuses to delete anyone who marked a session
        // submitted. Who filed a session is part of the evidence, and quietly nulling it would keep
        // the record while losing the half that says who is answerable for it. A session or a team
        // gets deleted in normal operation; an account effectively never does, so refusing is cheap.
        b.HasOne(s => s.SubmittedByUser)
            .WithMany()
            .HasForeignKey(s => s.SubmittedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The "has this session already been sent?" guard runs on every submission attempt and is the
        // last thing standing between a second press and a duplicate filing ARRL cannot undo.
        b.HasIndex(s => s.SessionId);
    }
}
