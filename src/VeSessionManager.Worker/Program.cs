using Microsoft.EntityFrameworkCore;
using Serilog;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Scheduling;
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

builder.Services.Configure<ZoomOptions>(builder.Configuration.GetSection(ZoomOptions.SectionName));
// Singleton so the cached OAuth token survives between poll cycles.
builder.Services.AddSingleton<IZoomClient, ZoomClient>();
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));
// Singleton so the bot login only happens once (bot tokens don't expire, unlike Zoom's).
builder.Services.AddSingleton<IDiscordEventClient, DiscordEventClient>();
builder.Services.AddScoped<SessionEventSchedulingService>();

builder.Services.AddScoped<JobRunHistoryLogger>();
builder.Services.AddHostedService<HelloWorldJob>();
builder.Services.AddHostedService<SessionIngestionJob>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();

    if (builder.Environment.IsDevelopment())
    {
        await DevDataSeeder.SeedAsync(dbContext, scope.ServiceProvider.GetRequiredService<ILogger<Program>>());
    }
}

host.Run();
