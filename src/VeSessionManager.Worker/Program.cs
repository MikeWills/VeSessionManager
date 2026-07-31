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
using VeSessionManager.Core.FccUls;
using VeSessionManager.Core.Ingestion;
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
builder.Services.Configure<ExamToolsOptions>(builder.Configuration.GetSection(ExamToolsOptions.SectionName));
// Singleton so the login cookie jar survives between poll cycles.
builder.Services.AddSingleton<IExamToolsClient, ExamToolsClient>();
builder.Services.AddScoped<SessionIngestionService>();
// Stateless (no DB/HTTP dependency of its own) since surge logic was removed — safe as a singleton.
builder.Services.AddSingleton<IngestionScheduleService>();
// Auto-detects a candidate's graded exam result from ExamTools — reuses IExamToolsClient, no new
// client/credentials needed. See docs/examtools-api.md's "Applicant exam results" section.
builder.Services.AddScoped<ExamResultSyncService>();

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

builder.Services.Configure<SquareOptions>(builder.Configuration.GetSection(SquareOptions.SectionName));
// Singleton: the Square SDK client owns its own HttpClient, same reasoning as the other API clients.
builder.Services.AddSingleton<ISquareClient, SquareClient>();
builder.Services.AddScoped<PaymentGenerationService>();
builder.Services.AddScoped<SquarePaymentLinkPurgeService>();

// No EmailOptions to Configure<> anymore — SmtpHost/Port/Username/Password/UseStartTls all live on
// Team now (multi-team, see docs/multi-team.md).
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<EmailTemplateRenderer>();
builder.Services.AddScoped<CandidateNotificationService>();

builder.Services.Configure<FccUlsOptions>(builder.Configuration.GetSection(FccUlsOptions.SectionName));
// Singleton: owns its own HttpClient, same reasoning as the other API clients. No credentials, so
// unlike Zoom/Square/Email this isn't an optional integration — it always runs.
builder.Services.AddSingleton<IFccUlsClient, FccUlsClient>();
builder.Services.AddScoped<FccUlsWatcherService>();

builder.Services.Configure<PaymentReminderOptions>(builder.Configuration.GetSection(PaymentReminderOptions.SectionName));
builder.Services.AddScoped<PaymentReminderService>();

// Phase 8: no job/worker involvement — a manual, user-triggered action + a dashboard query, both
// called directly by Phase 9's (not yet built) admin UI. Registered now so they're ready for it.
builder.Services.AddScoped<VecSubmissionService>();
// VecSubmissionReportService delegates its pending-count predicate to NavBadgeCountService (which
// the Web project also uses for the nav badges), so that has to be registered here too even though
// the Worker itself renders no nav.
builder.Services.AddScoped<NavBadgeCountService>();
builder.Services.AddScoped<VecSubmissionReportService>();

// Phase 10: also used by VeSessionManager.Web's Admin/SystemSettings page to edit the same row.
builder.Services.AddScoped<SystemSettingsService>();
builder.Services.AddScoped<PiiPurgeService>();

builder.Services.AddScoped<TeamSecretsMigrationService>();

builder.Services.AddScoped<JobRunHistoryLogger>();
builder.Services.AddHostedService<SessionIngestionJob>();
builder.Services.AddHostedService<DayBeforeReminderJob>();
builder.Services.AddHostedService<FccDailyWatcherJob>();
builder.Services.AddHostedService<FccWeeklyCatchupJob>();
builder.Services.AddHostedService<PaymentReminderJob>();
builder.Services.AddHostedService<SquareLinkPurgeJob>();
builder.Services.AddHostedService<PiiPurgeJob>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();

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
        return;
    }

    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await EmailDefaultsSeeder.SeedAsync(dbContext, startupLogger);

    if (builder.Environment.IsDevelopment())
    {
        await DevDataSeeder.SeedAsync(dbContext, startupLogger);
    }

    startupLogger.LogInformation("Ready to work!");
}

host.Run();
