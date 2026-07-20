using Microsoft.EntityFrameworkCore;
using Serilog;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.FccUls;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.Zoom;
using VeSessionManager.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<ExamToolsOptions>(builder.Configuration.GetSection(ExamToolsOptions.SectionName));
// Singleton so the login cookie jar survives between poll cycles.
builder.Services.AddSingleton<IExamToolsClient, ExamToolsClient>();
builder.Services.AddScoped<SessionIngestionService>();

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

builder.Services.AddScoped<JobRunHistoryLogger>();
builder.Services.AddHostedService<HelloWorldJob>();
builder.Services.AddHostedService<SessionIngestionJob>();
builder.Services.AddHostedService<DayBeforeReminderJob>();
builder.Services.AddHostedService<FccDailyWatcherJob>();
builder.Services.AddHostedService<FccWeeklyCatchupJob>();
builder.Services.AddHostedService<PaymentReminderJob>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();

    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await EmailDefaultsSeeder.SeedAsync(dbContext, startupLogger);

    if (builder.Environment.IsDevelopment())
    {
        await DevDataSeeder.SeedAsync(dbContext, startupLogger);
    }
}

host.Run();
