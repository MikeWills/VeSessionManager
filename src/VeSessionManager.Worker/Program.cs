using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VeSessionManager.Core;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Reconciliation;
using VeSessionManager.Core.Retention;
using VeSessionManager.Core.Uls;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Navigation;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.PiiPurge;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.VecSubmissions;
using VeSessionManager.Core.VolunteerExaminers;
using VeSessionManager.Core.Zoom;
using VeSessionManager.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Encrypts Team's credential columns at rest (2026-07-30, see EncryptedStringConverter) — the
// application name and key-ring path here MUST exactly match VeSessionManager.Web's own
// registration, or one process's writes become unreadable by the other. Key-ring path follows the
// same appsettings-per-environment convention as ConnectionStrings:DefaultConnection (see
// docs/deployment.md): outside the app's own synced path in Production, same reasoning as the DB
// file itself.
builder.Services.AddDataProtection()
    .SetApplicationName("VeSessionManager")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeyRingPath"] ?? "../../.dataprotection-keys"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
// Shared configuration, loaded BEFORE this host's own appsettings so a host can still override a
// value deliberately — it just cannot diverge by accident. Square/ExamTools used to be written out
// in every appsettings file, which is how Web ended up on Sandbox/examtools.dev while the Worker ran
// Production/alpha.exam.tools (T04). See src/Shared/appsettings.Shared.json.
// **Resolved against AppContext.BaseDirectory, not the content root.** The file is linked into each
// host from src/Shared and copied to the build output, so it sits beside the DLL — which is the
// content root for a published deployment, but NOT for `dotnet run`, where the content root is the
// project directory instead. Loading it by bare name therefore worked on the server and threw
// FileNotFoundException on every developer machine (found 2026-08-06, the first local run since the
// shared file landed). Kept `optional: false` deliberately: this file existing is what stops the two
// hosts drifting, so a missing one must fail loudly rather than silently reinstate T04.
var sharedConfigDirectory = AppContext.BaseDirectory;
builder.Configuration.AddJsonFile(Path.Combine(sharedConfigDirectory, "appsettings.Shared.json"), optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile(Path.Combine(sharedConfigDirectory, $"appsettings.Shared.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

builder.Services.Configure<ExamToolsOptions>(builder.Configuration.GetSection(ExamToolsOptions.SectionName));
// Singleton so the login cookie jar survives between poll cycles.
builder.Services.AddSingleton<IExamToolsClient, ExamToolsClient>();
builder.Services.AddScoped<SessionIngestionService>();
builder.Services.AddScoped<TeamPipeline>();
// Stateless (no DB/HTTP dependency of its own) since surge logic was removed — safe as a singleton.
builder.Services.AddSingleton<IngestionScheduleService>();
// Auto-detects a candidate's graded exam result from ExamTools — reuses IExamToolsClient, no new
// client/credentials needed. See docs/examtools-api.md's "Applicant exam results" section.
builder.Services.AddScoped<ExamResultSyncService>();

// Issue #67 part 2: drains the one-off historical-import queue Admin -> Team Maintenance writes to.
builder.Services.AddScoped<HistoricalImportService>();

// Phase 7: reuses IExamToolsClient, no new client/credentials needed.
builder.Services.AddScoped<VolunteerExaminerSyncService>();
builder.Services.AddScoped<VolunteerExaminerReportService>();

// Singleton so cached per-team OAuth tokens survive between poll cycles. No ZoomOptions to
// Configure<> anymore — AccountId/ClientId/ClientSecret/UserId all live on Team now (multi-team).
builder.Services.AddSingleton<IZoomClient, ZoomClient>();
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));
// Singleton so the bot login only happens once (bot tokens don't expire, unlike Zoom's). Only
// BotToken lives here now — it's shared across every team (multi-team, see docs/multi-team.md);
// each team's own Discord Guild lives on Team.DiscordGuildId instead.
builder.Services.AddSingleton<IDiscordEventClient, DiscordEventClient>();
builder.Services.AddScoped<SessionEventSchedulingService>();

// Singleton: the Square SDK client owns its own HttpClient, same reasoning as the other API clients.
builder.Services.AddSingleton<ISquareClient, SquareClient>();
// Singleton on purpose: the whole value of TeamIntegrationState is remembering which mute states it
// has already logged, across the scoped lifetimes background jobs create per tick. Scoped would make
// it log every tick, which is the behaviour it exists to prevent (#64).
builder.Services.AddSingleton<TeamIntegrationState>();

builder.Services.AddScoped<PaymentGenerationService>();
builder.Services.AddScoped<SquarePaymentLinkPurgeService>();

// No EmailOptions to Configure<> anymore — SmtpHost/Port/Username/Password/UseStartTls all live on
// Team now (multi-team, see docs/multi-team.md).
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<EmailTemplateRenderer>();
builder.Services.AddScoped<CandidateNotificationService>();

builder.Services.Configure<UlsLookupOptions>(builder.Configuration.GetSection(UlsLookupOptions.SectionName));
// Singleton: owns its own HttpClient, same reasoning as the other API clients. No credentials, so
// unlike Zoom/Square/Email this isn't an optional integration — it always runs.
builder.Services.AddSingleton<IUlsLookupClient, ExamToolsUlsLookupClient>();
builder.Services.AddScoped<UlsWatcherService>();
builder.Services.AddScoped<LicenseWatchService>();
builder.Services.AddScoped<VolunteerExaminerLicenseWatchService>();
builder.Services.AddScoped<ReconciliationService>();

builder.Services.Configure<PaymentReminderOptions>(builder.Configuration.GetSection(PaymentReminderOptions.SectionName));
builder.Services.AddScoped<PaymentReminderService>();

// Phase 8: no job/worker involvement — a manual, user-triggered action + a dashboard query, both
// called directly by Phase 9's (not yet built) admin UI. Registered now so they're ready for it.
builder.Services.AddScoped<VecSubmissionService>();
// Registered here as well as in Web so the Core service graph resolves identically in both hosts,
// even though the Worker renders no nav of its own.
builder.Services.AddScoped<NavBadgeCountService>();

// Phase 10: also used by VeSessionManager.Web's Admin/SystemSettings page to edit the same row.
builder.Services.AddScoped<SystemSettingsService>();
builder.Services.AddScoped<PiiPurgeService>();
// Resolved by PiiPurgeJob for the spent-token sweep (#303, D-03). The Worker had no registration for
// it at all — it was a Web-only service until the purge job started using it.
builder.Services.AddScoped<VeSelfServiceLinkService>();

builder.Services.AddScoped<RecordRetentionService>();

builder.Services.AddScoped<TeamSecretsMigrationService>();

builder.Services.AddScoped<JobRunHistoryLogger>();
builder.Services.AddHostedService<SessionIngestionJob>();
builder.Services.AddHostedService<DayBeforeReminderJob>();
builder.Services.AddHostedService<UlsWatcherJob>();
builder.Services.AddHostedService<LicenseWatchJob>();
builder.Services.AddHostedService<PaymentReminderJob>();
builder.Services.AddHostedService<SquareLinkPurgeJob>();
builder.Services.AddHostedService<PiiPurgeJob>();
builder.Services.AddHostedService<HistoricalImportJob>();
builder.Services.AddHostedService<ReconciliationJob>();
builder.Services.AddHostedService<RecordRetentionJob>();

// A mistyped switch used to be ignored in silence: the Worker started normally, did none of the
// one-off work that was asked for, and looked identical to a successful run. Checked before the
// host is even built so it costs nothing and cannot be missed.
string[] knownSwitches = ["--migrate-team-secrets", "--run-uls", "--verify-keyring"];
var unknownSwitches = args.Where(a => a.StartsWith("--") && !knownSwitches.Contains(a)).ToList();
if (unknownSwitches.Count > 0)
{
    Console.Error.WriteLine($"Unknown switch(es): {string.Join(", ", unknownSwitches)}");
    Console.Error.WriteLine($"Known switches: {string.Join(", ", knownSwitches)}");
    return 1;
}

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Read-only key-ring check, for confirming a restored key ring and database still match
    // (BackupScripts' runbooks/restore.md). The guard already runs on every normal startup — what
    // this adds is running it *without* starting the nine background jobs, which on restored data
    // would poll ExamTools, create Zoom/Discord events and mail real candidates. Before that, a
    // test restore had no safe way to prove itself.
    //
    // Deliberately skips Migrate(): a check meant to be safe on any schedule must not write to the
    // database it is checking, and a restored backup older than this binary should be *reported*,
    // not silently upgraded by the act of verifying it.
    if (args.Contains("--verify-keyring"))
    {
        var verifyLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        return await KeyRingVerification.RunAsync(dbContext, verifyLogger, Console.Error);
    }

    dbContext.Database.Migrate();

    // Fails the host rather than running with credentials it cannot read — see
    // DataProtectionKeyRingGuard. Deliberately before the one-off switches below: a
    // --migrate-team-secrets run against the wrong key ring would rewrite every credential with the
    // undecryptable value it just read back, destroying the originals.
    var keyRingLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DataProtectionKeyRingGuard.VerifyAsync(dbContext, keyRingLogger);

    // Surfaces a credential that carries the Data Protection marker but will not decrypt (#160).
    // The guard above catches that at startup; this covers anything that only becomes
    // readable-but-wrong later, and makes the fallback's silence visible either way.
    EncryptedStringConverter.OnUndecryptableValueRead ??= message => keyRingLogger.LogError("{Message}", message);

    // One-off, human-triggered CLI flag (2026-07-30, see TeamSecretsMigrationService) — never runs
    // automatically on a normal startup, since it touches every real team's live external-service
    // credentials. Exits immediately after, rather than falling through to the normal
    // BackgroundService startup below. Safe to re-run (see TeamSecretsMigrationService's own remarks
    // on backup-restore/interrupted-run/stray-plaintext recovery scenarios).
    if (args.Contains("--migrate-team-secrets"))
    {
        var migrationLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        migrationLogger.LogInformation("Running one-off team secrets migration (--migrate-team-secrets)...");
        var migrationService = scope.ServiceProvider.GetRequiredService<TeamSecretsMigrationService>();
        await migrationService.MigrateAsync(CancellationToken.None);
        migrationLogger.LogInformation("Team secrets migration complete — exiting without starting the normal Worker jobs.");
        return 0;
    }

    // Human-triggered ULS watcher run. Same exit-immediately shape as the flag above. Replaced
    // --run-fcc-daily/--run-fcc-weekly/--run-fcc-all-dailies on 2026-07-31: those three existed
    // because each FCC day-name file was a separate one-shot window that could be missed
    // permanently, so recovery meant choosing *which* file to re-read. A ULS lookup returns current
    // state on every call, so there is nothing to choose — one switch covers every case, including
    // historical recovery. Idempotent: the watcher only ever touches non-terminal candidates.
    if (args.Contains("--run-uls"))
    {
        var ulsLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var watcher = scope.ServiceProvider.GetRequiredService<UlsWatcherService>();
        var jobRunHistoryLogger = scope.ServiceProvider.GetRequiredService<JobRunHistoryLogger>();

        ulsLogger.LogInformation("Running ULS watcher on demand (--run-uls)...");
        await jobRunHistoryLogger.RunAsync("UlsWatcher", watcher.RunAsync, null, CancellationToken.None);
        ulsLogger.LogInformation("On-demand ULS run complete — exiting without starting the normal Worker jobs.");
        return 0;
    }

    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await EmailDefaultsSeeder.SeedAsync(dbContext, startupLogger);
    // Before DevDataSeeder, which reuses the ARRL row this creates rather than making a second one.
    await VecDefaultsSeeder.SeedAsync(dbContext, startupLogger);

    if (builder.Environment.IsDevelopment())
    {
        await DevDataSeeder.SeedAsync(dbContext, startupLogger);
    }

    startupLogger.LogInformation("Ready to work!");
}

host.Run();
return 0;
