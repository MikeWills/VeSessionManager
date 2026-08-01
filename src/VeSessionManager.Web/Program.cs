using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VeSessionManager.Core;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.CandidateActions;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Navigation;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.VecSubmissions;
using VeSessionManager.Core.VolunteerExaminers;
using VeSessionManager.Core.Zoom;
using VeSessionManager.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Encrypts Team's credential columns at rest (2026-07-30, see EncryptedStringConverter) — the
// application name and key-ring path here MUST exactly match VeSessionManager.Worker's own
// registration, or one process's writes become unreadable by the other. Key-ring path follows the
// same appsettings-per-environment convention as ConnectionStrings:DefaultConnection (see
// docs/deployment.md): outside the app's own synced path in Production, same reasoning as the DB
// file itself.
builder.Services.AddDataProtection()
    .SetApplicationName("VeSessionManager")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeyRingPath"] ?? "../../.dataprotection-keys"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.Configure<SquareOptions>(builder.Configuration.GetSection(SquareOptions.SectionName));
// Singleton: the Square SDK client owns its own HttpClient, same reasoning as the Worker's own
// registration — CandidateActionService.CreateRetestPaymentAsync needs PaymentGenerationService,
// which needs this, for the "create retest payment" admin action.
builder.Services.AddSingleton<ISquareClient, SquareClient>();
builder.Services.AddScoped<PaymentGenerationService>();
// Backs the public, unauthenticated youth-rate confirmation page (Pages/Public/YouthConfirm).
builder.Services.AddScoped<YouthPaymentConfirmationService>();
// WebhookSignatureKey/WebhookNotificationUrl live on Team (multi-team, each team verifies against
// its own key via the /webhooks/square/{teamId} route) — nothing else in this project needs
// SquareOptions:Environment beyond what SquareClient itself reads above.
builder.Services.AddScoped<SquareWebhookHandler>();
// Unmatched-order auto/manual matching + "complete the Square order once paid and the session's
// done" — used by SquareWebhookHandler above and by SessionActionService below.
builder.Services.AddScoped<SquarePaymentMatchingService>();

builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<EmailTemplateRenderer>();
builder.Services.AddScoped<CandidateNotificationService>();

builder.Services.AddScoped<VecSubmissionService>();
builder.Services.AddScoped<VolunteerExaminerReportService>();

// Pending-work counts shown as badges on the app nav (_AppLayout.cshtml); also the single source of
// the pending-VEC-submission predicate VecSubmissionReportService delegates to.
builder.Services.AddScoped<NavBadgeCountService>();

// "Refresh candidates" button on the session detail page (Pages/SessionManager/Detail.cshtml.cs) —
// same per-team pipeline as the Worker's SessionIngestionJob, run on demand instead of waiting for
// its next tick. Registrations below mirror VeSessionManager.Worker/Program.cs's own (same
// singleton-vs-scoped reasoning in each comment there).
builder.Services.Configure<ExamToolsOptions>(builder.Configuration.GetSection(ExamToolsOptions.SectionName));
builder.Services.AddSingleton<IExamToolsClient, ExamToolsClient>();
builder.Services.AddScoped<SessionIngestionService>();
builder.Services.AddScoped<VolunteerExaminerSyncService>();
builder.Services.AddSingleton<IZoomClient, ZoomClient>();
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));
builder.Services.AddSingleton<IDiscordEventClient, DiscordEventClient>();
builder.Services.AddScoped<SessionEventSchedulingService>();
builder.Services.AddScoped<JobRunHistoryLogger>();
builder.Services.AddScoped<ManualCandidateRefreshService>();

// Issues #77/#73: Admin → Team Maintenance (team-level "Refresh now" + ingestion schedule
// visibility) and the site-wide Worker-health banner. IngestionScheduleService is the same gate the
// Worker uses, registered here so the UI's countdown is derived from it rather than restated.
// IngestionHealthCache is a SINGLETON on purpose (it caches across requests) and therefore resolves
// IngestionStatusService through a fresh scope rather than by injection — see its own remarks.
builder.Services.AddScoped<IngestionScheduleService>();
builder.Services.AddScoped<IngestionStatusService>();
builder.Services.AddScoped<TeamRefreshThrottle>();
builder.Services.AddSingleton<IngestionHealthCache>();

// Phase 9b: the actual UI-triggered wiring for every Session Manager action — see
// Pages/SessionManager/Detail.cshtml.cs.
builder.Services.AddScoped<CandidateActionService>();
builder.Services.AddScoped<SessionActionService>();
builder.Services.AddScoped<VolunteerExaminerRosterService>();

// Phase 9a: stateless, no DB dependency of its own — safe as a singleton.
builder.Services.AddSingleton<SessionAccessScope>();

// Phase 9c: Admin Config Screens — SystemAdmin/TeamAdmin config surface (Pages/Admin/).
builder.Services.AddSingleton<AdminAccessScope>();
builder.Services.AddScoped<TeamSettingsService>();
builder.Services.AddScoped<VecManagementService>();
builder.Services.AddScoped<FeeConfigurationService>();
builder.Services.AddScoped<EmailTemplateAdminService>();
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<SystemSettingsService>();

// AddIdentityCore, not AddIdentity — deliberately skips Identity's own Role tables (Role stays one
// plain enum column on User, see docs/admin-auth.md). AddIdentityCookies() (below) supplies the
// ApplicationScheme/ExternalScheme cookie schemes that AddIdentity would otherwise add for you.
builder.Services.AddIdentityCore<User>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false; // no email-confirmation infra exists yet
        // Reviewed 2026-07-28 now that real accounts exist (was a placeholder default, not something
        // specifically requested). RequiredLength bumped 10 -> 12 per NIST 800-63B (length matters
        // more than composition rules) — RequireDigit/RequireLowercase/RequireUppercase are left at
        // Identity's own true defaults (not overridden here) as extra friction on top, since these
        // are admin/VE accounts, not public self-service ones.
        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    // Default (SameAsRequest) would silently send the auth cookie over plain HTTP if a proxy in
    // front of this app ever got misconfigured — pin it to HTTPS-only outside Development. Left at
    // the default in Development since local dev serves plain HTTP by default (see CLAUDE.md's
    // launch-profile note) and an Always-Secure cookie would never come back to the browser there.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

// AddIdentityCookies() returns an IdentityCookiesBuilder, not the AuthenticationBuilder itself —
// keep the original reference so .AddGoogle()/.AddMicrosoftAccount() below (AuthenticationBuilder
// extension methods) have something to chain onto.
var authenticationBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
});
authenticationBuilder.AddIdentityCookies();

// Google/Microsoft are registered conditionally — same optional-integration pattern as every other
// external credential in this app (Zoom/Discord/Square/Email): no ClientId/ClientSecret yet just
// means that sign-in button doesn't render, never a startup failure.
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authenticationBuilder.AddGoogle(options =>
    {
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
        // Not mapped by this handler's own defaults — ExternalLoginCallbackModel checks this before
        // trusting an email-claim match enough to link/sign in to an existing local account.
        options.ClaimActions.MapJsonKey("email_verified", "email_verified");
    });
}

var microsoftClientId = builder.Configuration["Authentication:Microsoft:ClientId"];
var microsoftClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
if (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret))
{
    authenticationBuilder.AddMicrosoftAccount(options =>
    {
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.ClientId = microsoftClientId;
        options.ClientSecret = microsoftClientSecret;
    });
}

// Add services to the container.
builder.Services.AddRazorPages();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();

    if (app.Environment.IsDevelopment())
    {
        var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await DevAuthSeeder.SeedAsync(scope.ServiceProvider, startupLogger);
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

// Was missing entirely before Phase 9a — UseAuthorization() alone never populated HttpContext.User,
// so it had been a silent no-op since Phase 0's scaffold.
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapSquareWebhook();

app.Run();
