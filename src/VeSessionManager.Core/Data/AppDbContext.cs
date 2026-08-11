using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data;

/// <summary>
/// Base changed from plain DbContext to IdentityUserContext&lt;User, int&gt; in Phase 9a — gives
/// Users/UserClaims/UserLogins/UserTokens for ASP.NET Core Identity (external logins need
/// UserLogins) without the unused Identity Role tables IdentityDbContext would also add (Role
/// stays one plain enum column on User, not Identity's own Role system — see docs/admin-auth.md).
///
/// IDataProtectionProvider (2026-07-30, see EncryptedStringConverter): Web/Worker's real DI
/// registration always supplies one backed by a shared, persisted key ring (both processes must
/// register the exact same application name + key-ring path — see Program.cs in each — or one
/// process's writes become unreadable by the other). The parameterless-provider overload below
/// exists purely so the ~30 existing test files constructing `new AppDbContext(options)` directly
/// don't all need updating — each gets its own fresh, non-persisted key, which is fine since no
/// test ever reads encrypted Team data across two separate AppDbContext instances.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, IDataProtectionProvider dataProtectionProvider) : IdentityUserContext<User, int>(options)
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : this(options, new EphemeralDataProtectionProvider())
    {
    }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Vec> Vecs => Set<Vec>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();
    public DbSet<FeeConfiguration> FeeConfigurations => Set<FeeConfiguration>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<VolunteerExaminer> VolunteerExaminers => Set<VolunteerExaminer>();
    public DbSet<SessionVolunteerExaminer> SessionVolunteerExaminers => Set<SessionVolunteerExaminer>();
    public DbSet<VeTeamMembership> VeTeamMemberships => Set<VeTeamMembership>();
    public DbSet<VeTag> VeTags => Set<VeTag>();
    public DbSet<VeTagAssignment> VeTagAssignments => Set<VeTagAssignment>();
    public DbSet<VeVecAccreditation> VeVecAccreditations => Set<VeVecAccreditation>();
    public DbSet<VeCallSignHistory> VeCallSignHistories => Set<VeCallSignHistory>();
    public DbSet<VeSelfServiceToken> VeSelfServiceTokens => Set<VeSelfServiceToken>();
    public DbSet<VeEmailChangeRequest> VeEmailChangeRequests => Set<VeEmailChangeRequest>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<JobRunHistory> JobRunHistories => Set<JobRunHistory>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<UnmatchedSquarePayment> UnmatchedSquarePayments => Set<UnmatchedSquarePayment>();
    public DbSet<UserTeam> UserTeams => Set<UserTeam>();
    public DbSet<HistoricalImportRequest> HistoricalImportRequests => Set<HistoricalImportRequest>();
    public DbSet<WatchedLicense> WatchedLicenses => Set<WatchedLicense>();
    public DbSet<ReconciliationFinding> ReconciliationFindings => Set<ReconciliationFinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Required so IdentityUserContext's own entity configuration (Users/UserClaims/UserLogins/
        // UserTokens table mapping and indexes) actually applies.
        base.OnModelCreating(modelBuilder);

        // The app never hard-deletes rows with dependents (PII is nulled in place, not the row
        // removed — see Candidate/Payment purge behavior in the spec), so every FK below is
        // Restrict rather than Cascade. This also sidesteps SQL Server-style "multiple cascade
        // paths" errors from the several relationships that both point at User/Vec.
        modelBuilder.Entity<FeeConfiguration>(b =>
        {
            b.HasOne(f => f.Vec).WithMany(v => v.FeeConfigurations).HasForeignKey(f => f.VecId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(f => f.CreatedByUser).WithMany().HasForeignKey(f => f.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.Property(f => f.ExamFeeAmount).HasPrecision(10, 2);
            b.Property(f => f.RetainedAmount).HasPrecision(10, 2);
            b.Property(f => f.YouthExamFeeAmount).HasPrecision(10, 2);
        });

        modelBuilder.Entity<Session>(b =>
        {
            b.HasIndex(s => s.ExamToolsSessionId).IsUnique();
            b.HasOne(s => s.Vec).WithMany(v => v.Sessions).HasForeignKey(s => s.VecId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.Team).WithMany(t => t.Sessions).HasForeignKey(s => s.TeamId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.FeeConfiguration).WithMany(f => f.Sessions).HasForeignKey(s => s.FeeConfigurationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.TestingCompletedByUser).WithMany().HasForeignKey(s => s.TestingCompletedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(s => s.VecSubmittedByUser).WithMany().HasForeignKey(s => s.VecSubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
            // NOTE: RetainedAmountOverrideByUser is the one User FK here still left to EF's
            // convention (ClientSetNull), which contradicts this block's opening statement that every
            // FK is Restrict. Audit T21 asked for it to be pinned, and it was — then reverted, on
            // purpose:
            //
            //   * SQLite implements an FK change as a full table rebuild, which EF reports as "cannot
            //     be executed in a transaction". An interrupted deploy would leave the database
            //     partially migrated and needing manual repair.
            //   * It guards against a user being deleted — and there is no delete path in this app at
            //     all. UserManagementService only deactivates; see #188, which is still open.
            //
            // So the risk is real today and the benefit is not. #188 has to decide FK behaviour across
            // thirteen Restrict relationships anyway; this one belongs in that migration, alone, where
            // the rebuild can be planned rather than ridden along with an index change.
            // Money, so two decimal places rather than the provider's default. Matches
            // FeeConfiguration's amounts above.
            b.Property(s => s.RetainedAmountOverride).HasPrecision(10, 2);
            // The session list's default ordering and its date-range filter, per team — the busiest
            // query in the app.
            b.HasIndex(s => new { s.TeamId, s.ScheduledStartUtc });
        });

        modelBuilder.Entity<JobRunHistory>(b =>
        {
            b.HasOne(j => j.Team).WithMany().HasForeignKey(j => j.TeamId).OnDelete(DeleteBehavior.Restrict);
            // How the ops dashboard reads this table: one job's recent runs, for one team, newest
            // first. The table only grows, so an unindexed scan gets slower every day it works.
            b.HasIndex(j => new { j.TeamId, j.JobName, j.StartedUtc });
        });

        modelBuilder.Entity<Candidate>(b =>
        {
            b.HasIndex(c => new { c.SessionId, c.ExamToolsApplicantId }).IsUnique();
            // Applicant Status and the ULS watcher both select by status across every session, and
            // the terminal statuses are the majority — so this is a filter that removes most rows,
            // which is exactly when an index earns its place.
            b.HasIndex(c => c.ApplicationStatus);
            b.HasOne(c => c.Session).WithMany(s => s.Candidates).HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(c => c.ResultMarkedByUser).WithMany().HasForeignKey(c => c.ResultMarkedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payment>(b =>
        {
            b.HasOne(p => p.Candidate).WithMany(c => c.Payments).HasForeignKey(p => p.CandidateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(p => p.RefundRequestedByUser).WithMany().HasForeignKey(p => p.RefundRequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            // The Square webhook's only lookup, and it runs against Square's response deadline: an
            // unindexed scan of every payment ever taken is not something to leave on that path.
            // Not unique — see the note below on nulls, and a refunded/re-created link can repeat.
            b.HasIndex(p => p.SquarePaymentReferenceId);
            b.Property(p => p.Amount).HasPrecision(10, 2);
            b.Property(p => p.SquareAmountPaidUsd).HasPrecision(10, 2);
            // SQLite treats NULLs as distinct in a unique index, so multiple Payments with a null
            // token (the common case — only sessions under a youth-program Vec ever get one) are
            // fine; only a real, generated token collision would violate this.
            b.HasIndex(p => p.YouthConfirmationToken).IsUnique();

            // One InitialExam payment per candidate, enforced by the database (2026-08-03).
            // PaymentGenerationService decides whether to create one by checking
            // "!c.Payments.Any(p => p.Reason == InitialExam)" — a read that the Web process (manual
            // refresh) and the Worker (scheduled tick) can both perform before either one saves,
            // concluding independently that no payment exists. The result was two Unpaid rows, two
            // live Square checkout links, and later two reminder emails for one candidate. Nothing
            // in the schema prevented it.
            //
            // Filtered to InitialExam because a Retest payment legitimately repeats — a candidate
            // may sit (and pay for) several retests. The filter is written from the enum value
            // rather than a hardcoded 0 so it cannot silently drift if the enum is ever renumbered.
            //
            // The index converts an invisible double-charge into a caught constraint violation,
            // which PaymentGenerationService handles per-candidate as "the other process already
            // created it" rather than as an error.
            b.HasIndex(p => new { p.CandidateId, p.Reason })
                .IsUnique()
                .HasFilter($"\"Reason\" = {(int)PaymentReason.InitialExam}");
        });

        modelBuilder.Entity<SessionVolunteerExaminer>(b =>
        {
            b.HasKey(sve => new { sve.SessionId, sve.VolunteerExaminerId });
            b.HasOne(sve => sve.Session).WithMany(s => s.SessionVolunteerExaminers).HasForeignKey(sve => sve.SessionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(sve => sve.VolunteerExaminer).WithMany(v => v.SessionVolunteerExaminers).HasForeignKey(sve => sve.VolunteerExaminerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VolunteerExaminer>(b =>
        {
            // FRN is unique where present — it is the stable identity, and two people cannot share
            // one. Filtered, because almost every row has none: ExamTools never reports an FRN, so
            // it only arrives once the ULS sweep backfills it. SQLite treats NULLs as distinct in a
            // unique index anyway; the filter states the intent for a reader.
            b.HasIndex(v => v.Frn).IsUnique().HasFilter("\"Frn\" IS NOT NULL");

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
        });

        modelBuilder.Entity<VeTeamMembership>(b =>
        {
            b.HasIndex(m => new { m.VolunteerExaminerId, m.TeamId }).IsUnique();
            b.HasOne(m => m.VolunteerExaminer).WithMany(v => v.TeamMemberships).HasForeignKey(m => m.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(m => m.Team).WithMany().HasForeignKey(m => m.TeamId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VeTag>(b =>
        {
            b.HasIndex(t => new { t.TeamId, t.Name }).IsUnique();
            b.HasOne(t => t.Team).WithMany().HasForeignKey(t => t.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VeTagAssignment>(b =>
        {
            b.HasKey(a => new { a.VeTeamMembershipId, a.VeTagId });
            b.HasOne(a => a.VeTeamMembership).WithMany(m => m.TagAssignments).HasForeignKey(a => a.VeTeamMembershipId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.VeTag).WithMany(t => t.Assignments).HasForeignKey(a => a.VeTagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VeVecAccreditation>(b =>
        {
            b.HasIndex(a => new { a.VolunteerExaminerId, a.VecId }).IsUnique();
            b.HasOne(a => a.VolunteerExaminer).WithMany(v => v.VecAccreditations).HasForeignKey(a => a.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Vec).WithMany().HasForeignKey(a => a.VecId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VeSelfServiceToken>(b =>
        {
            // Unique: a presented token resolves to exactly one row or none. A collision would be a
            // 256-bit coincidence, but a unique index turns "cannot happen" into "cannot be stored".
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasOne(t => t.VolunteerExaminer).WithMany().HasForeignKey(t => t.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VeEmailChangeRequest>(b =>
        {
            b.HasIndex(r => r.TokenHash).IsUnique();
            b.HasOne(r => r.VolunteerExaminer).WithMany().HasForeignKey(r => r.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VeCallSignHistory>(b =>
        {
            // Not unique: a call sign can legitimately appear twice — released by one person and
            // later reissued to another — and this table is the record of that, not a constraint on it.
            b.HasIndex(h => h.CallSign);
            b.HasOne(h => h.VolunteerExaminer).WithMany(v => v.CallSignHistory).HasForeignKey(h => h.VolunteerExaminerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.HasOne(u => u.ManagedByUser).WithMany().HasForeignKey(u => u.ManagedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserTeam>(b =>
        {
            b.HasKey(ut => new { ut.UserId, ut.TeamId });
            b.HasOne(ut => ut.User).WithMany(u => u.UserTeams).HasForeignKey(ut => ut.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(ut => ut.Team).WithMany(t => t.UserTeams).HasForeignKey(ut => ut.TeamId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HistoricalImportRequest>(b =>
        {
            b.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(r => r.RequestedByUser).WithMany().HasForeignKey(r => r.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            // The Worker's only query is "oldest Pending", and the page's is "this team's requests".
            b.HasIndex(r => new { r.Status, r.RequestedUtc });
            b.HasIndex(r => r.TeamId);
        });

        modelBuilder.Entity<WatchedLicense>(b =>
        {
            b.HasOne(w => w.Team).WithMany().HasForeignKey(w => w.TeamId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(w => w.AddedByUser).WithMany().HasForeignKey(w => w.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
            // Uniqueness is per team, not global: two teams may each independently watch the same
            // call sign, and neither should be able to see or clobber the other's row.
            b.HasIndex(w => new { w.TeamId, w.CallSign }).IsUnique();
            // The refresh job's query is "least recently checked first", across all teams.
            b.HasIndex(w => w.LastCheckedUtc);
        });

        modelBuilder.Entity<EmailTemplate>(b =>
        {
            // Per-team customizable templates (multi-team) — uniqueness is now (TeamId, Key), not
            // Key alone.
            b.HasIndex(e => new { e.TeamId, e.Key }).IsUnique();
            b.HasOne(e => e.Team).WithMany().HasForeignKey(e => e.TeamId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(e => e.UpdatedByUser).WithMany().HasForeignKey(e => e.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EmailSettings>(b =>
        {
            // One row per team (multi-team) — was a true singleton before.
            b.HasIndex(e => e.TeamId).IsUnique();
            b.HasOne(e => e.Team).WithMany().HasForeignKey(e => e.TeamId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(e => e.UpdatedByUser).WithMany().HasForeignKey(e => e.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
            // The audit page orders by this and nothing else, over a table that is append-only and
            // never pruned (see #86, which is about that lack of retention).
            b.HasIndex(a => a.TimestampUtc);
        });

        modelBuilder.Entity<SystemSettings>(b =>
        {
            b.HasOne(s => s.UpdatedByUser).WithMany().HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
            // Encrypted like Team's credential columns, under the same protector purpose so there is
            // one key path to back up rather than two. IsSystemEmailConfigured is a computed
            // property with no setter, so EF ignores it without being told to.
            b.Property(s => s.SystemSmtpPassword)
                .HasConversion(new EncryptedStringConverter(
                    dataProtectionProvider.CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose)));
        });

        // Phase 9c adds real Team/Vec create screens for the first time — enforce name
        // uniqueness so the new team-picker/VEC-picker dropdowns can't end up with duplicates.
        var teamCredentialsProtector = dataProtectionProvider.CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose);
        var encryptedString = new EncryptedStringConverter(teamCredentialsProtector);
        modelBuilder.Entity<Team>(b =>
        {
            b.HasIndex(t => t.Name).IsUnique();
            // C#'s "= 30" property initializer only applies to newly-constructed objects — without
            // this, the SQL column default (used for any row inserted outside EF, and by the
            // migration's own AddColumn for existing rows) would be 0, which means "purge
            // immediately" instead of "not configured yet."
            b.Property(t => t.PurgeUnpaidLinkDays).HasDefaultValue(30);
            // Same reasoning — without this, existing teams would retroactively get 0 (no breakout
            // rooms) from the migration's AddColumn instead of the intended default of 2.
            b.Property(t => t.ZoomBreakoutRoomCount).HasDefaultValue(2);
            // Sandbox is already 0, so this changes no value — it declares the SQL default so a
            // schema built from the model (EnsureCreated, i.e. the SQLite tests) matches the one the
            // migration actually produces. Without it a row inserted outside EF hits a NOT NULL
            // failure on one and succeeds on the other, which is drift that only shows up in tests.
            b.Property(t => t.SquareEnvironment).HasDefaultValue(SquareApiEnvironment.Sandbox);

            // Encrypted at rest (2026-07-30 security review) — genuine bearer secrets only, not the
            // usernames/ids/URLs alongside them (those stay plaintext, useful to read at a glance).
            // See EncryptedStringConverter's remarks and TeamSecretsMigrationService for existing data.
            b.Property(t => t.ExamToolsPassword).HasConversion(encryptedString);
            b.Property(t => t.ZoomClientSecret).HasConversion(encryptedString);
            b.Property(t => t.SquareAccessToken).HasConversion(encryptedString);
            b.Property(t => t.SquareWebhookSignatureKey).HasConversion(encryptedString);
            b.Property(t => t.SmtpPassword).HasConversion(encryptedString);
        });
        modelBuilder.Entity<Vec>(b =>
        {
            b.HasIndex(v => v.Name).IsUnique();
            // Two VECs claiming the same ExamTools code would make ingestion's match ambiguous.
            // SQLite treats NULLs as distinct in a unique index, so the many rows that leave this
            // null (code == name) don't collide with each other.
            b.HasIndex(v => v.ExamToolsCode).IsUnique();
        });

        modelBuilder.Entity<UnmatchedSquarePayment>(b =>
        {
            // Guards against a duplicate row for the same order id (e.g. a Square webhook
            // redelivery arriving before a human resolves the first one) — see
            // SquarePaymentMatchingService.HandleUnmatchedOrderAsync.
            b.HasIndex(u => new { u.TeamId, u.SquareOrderId }).IsUnique();
            b.HasOne(u => u.Team).WithMany().HasForeignKey(u => u.TeamId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(u => u.ResolvedByUser).WithMany().HasForeignKey(u => u.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(u => u.MatchedPayment).WithMany().HasForeignKey(u => u.MatchedPaymentId).OnDelete(DeleteBehavior.Restrict);
            b.Property(u => u.AmountUsd).HasPrecision(10, 2);
        });

        modelBuilder.Entity<ReconciliationFinding>(b =>
        {
            // One standing row per (team, kind, remote session) — the sweep refreshes rather than
            // re-adds, so a discrepancy that persists for a month is one finding, not thirty.
            // Unique because a duplicate would double-count the badge, which is the one number here
            // anybody reads at a glance.
            b.HasIndex(f => new { f.TeamId, f.Kind, f.ExamToolsSessionId }).IsUnique();

            // The findings page and the badge both filter on "still open".
            b.HasIndex(f => new { f.TeamId, f.ResolvedUtc });

            b.HasOne(f => f.Team).WithMany().HasForeignKey(f => f.TeamId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
