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
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<JobRunHistory> JobRunHistories => Set<JobRunHistory>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<UnmatchedSquarePayment> UnmatchedSquarePayments => Set<UnmatchedSquarePayment>();
    public DbSet<UserTeam> UserTeams => Set<UserTeam>();
    public DbSet<HistoricalImportRequest> HistoricalImportRequests => Set<HistoricalImportRequest>();

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
        });

        modelBuilder.Entity<JobRunHistory>(b =>
        {
            b.HasOne(j => j.Team).WithMany().HasForeignKey(j => j.TeamId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Candidate>(b =>
        {
            b.HasIndex(c => new { c.SessionId, c.ExamToolsApplicantId }).IsUnique();
            b.HasOne(c => c.Session).WithMany(s => s.Candidates).HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(c => c.ResultMarkedByUser).WithMany().HasForeignKey(c => c.ResultMarkedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payment>(b =>
        {
            b.HasOne(p => p.Candidate).WithMany(c => c.Payments).HasForeignKey(p => p.CandidateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(p => p.RefundRequestedByUser).WithMany().HasForeignKey(p => p.RefundRequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            b.Property(p => p.Amount).HasPrecision(10, 2);
            b.Property(p => p.SquareAmountPaidUsd).HasPrecision(10, 2);
            // SQLite treats NULLs as distinct in a unique index, so multiple Payments with a null
            // token (the common case — only sessions under a youth-program Vec ever get one) are
            // fine; only a real, generated token collision would violate this.
            b.HasIndex(p => p.YouthConfirmationToken).IsUnique();
        });

        modelBuilder.Entity<SessionVolunteerExaminer>(b =>
        {
            b.HasKey(sve => new { sve.SessionId, sve.VolunteerExaminerId });
            b.HasOne(sve => sve.Session).WithMany(s => s.SessionVolunteerExaminers).HasForeignKey(sve => sve.SessionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(sve => sve.VolunteerExaminer).WithMany(v => v.SessionVolunteerExaminers).HasForeignKey(sve => sve.VolunteerExaminerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VolunteerExaminer>(b =>
        {
            // A VE is matched by (TeamId, CallSign) during roster sync — see VolunteerExaminerSyncService.
            b.HasIndex(v => new { v.TeamId, v.CallSign }).IsUnique();
            b.HasOne(v => v.Team).WithMany().HasForeignKey(v => v.TeamId).OnDelete(DeleteBehavior.Restrict);
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
        });

        modelBuilder.Entity<SystemSettings>(b =>
        {
            b.HasOne(s => s.UpdatedByUser).WithMany().HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
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
    }
}
