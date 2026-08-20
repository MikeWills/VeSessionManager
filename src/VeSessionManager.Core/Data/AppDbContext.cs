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
    /// <summary>What was filed with ARRL-VEC and what came back (#197). A row exists even for an
    /// unconfirmed attempt — that is the case it matters most for.</summary>
    public DbSet<ArrlVecSubmission> ArrlVecSubmissions => Set<ArrlVecSubmission>();

    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<Payment> Payments => Set<Payment>();

    /// <summary>Sessions ingestion is currently refusing for want of configuration (#440) — a statement about the present, swept each run.</summary>
    public DbSet<SkippedSession> SkippedSessions => Set<SkippedSession>();

    /// <summary>Stored FCC application timelines (#195) — written only when the ULS lookup reports something different.</summary>
    public DbSet<CandidateUlsHistoryEntry> CandidateUlsHistoryEntries => Set<CandidateUlsHistoryEntry>();
    public DbSet<CandidateEmailSend> CandidateEmailSends => Set<CandidateEmailSend>();
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
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<UserTeam> UserTeams => Set<UserTeam>();
    public DbSet<HistoricalImportRequest> HistoricalImportRequests => Set<HistoricalImportRequest>();
    public DbSet<WatchedLicense> WatchedLicenses => Set<WatchedLicense>();
    public DbSet<ReconciliationFinding> ReconciliationFindings => Set<ReconciliationFinding>();
    public DbSet<MessageRule> MessageRules => Set<MessageRule>();
    public DbSet<MessageRuleRun> MessageRuleRuns => Set<MessageRuleRun>();

    /// <summary>
    /// Was 340 lines and 27 inline entity blocks; the per-entity rules now live in
    /// <c>Data/Configurations</c> (#311, S-04). Two recent changes had each added index configuration
    /// to the middle of that method, which is what made splitting it worth doing rather than tidy.
    ///
    /// <para><b>The whole-model convention, which is not any one entity's business:</b> the app never
    /// hard-deletes rows with dependents — PII is nulled in place, the row is not removed (see the
    /// Candidate/Payment purge behavior in the spec) — so every foreign key is <c>Restrict</c>
    /// rather than <c>Cascade</c>. That also sidesteps SQL Server-style "multiple cascade paths"
    /// errors from the several relationships that both point at User/Vec.</para>
    ///
    /// <para><b>Team and SystemSettings are applied by hand</b> because their configurations need the
    /// encrypted-string converter, built from the injected <c>IDataProtectionProvider</c>.
    /// <c>ApplyConfigurationsFromAssembly</c> constructs configurations with no arguments, so an
    /// entity whose rules depend on a runtime service cannot be discovered that way — it is excluded
    /// from the scan by the filter below rather than being registered twice.</para>
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Required so IdentityUserContext's own entity configuration (Users/UserClaims/UserLogins/
        // UserTokens table mapping and indexes) actually applies. First, so the configurations below
        // refine it rather than being overwritten by it.
        base.OnModelCreating(modelBuilder);

        var teamCredentialsProtector = dataProtectionProvider.CreateProtector(EncryptedStringConverter.TeamCredentialsPurpose);
        var encryptedString = new EncryptedStringConverter(teamCredentialsProtector);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AppDbContext).Assembly,
            type => type != typeof(Configurations.TeamConfiguration)
                 && type != typeof(Configurations.SystemSettingsConfiguration));

        modelBuilder.ApplyConfiguration(new Configurations.TeamConfiguration(encryptedString));
        modelBuilder.ApplyConfiguration(new Configurations.SystemSettingsConfiguration(encryptedString));
    }
}
