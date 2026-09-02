using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
using VeSessionManager.Core.Integrations;
using VeSessionManager.Core.ExamResults;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Navigation;
using VeSessionManager.Core.Messaging;
using VeSessionManager.Core.Messaging.Scanners;
using VeSessionManager.Core.Notifications;
using VeSessionManager.Core.Payments;
using VeSessionManager.Core.Reporting;
using VeSessionManager.Core.Scheduling;
using VeSessionManager.Core.Sessions;
using VeSessionManager.Core.Square;
using VeSessionManager.Core.Uls;
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

// Singleton: the Square SDK client owns its own HttpClient, same reasoning as the Worker's own
// registration — CandidateActionService.CreateRetestPaymentAsync needs PaymentGenerationService,
// which needs this, for the "create retest payment" admin action.
builder.Services.AddSingleton<ISquareClient, SquareClient>();
// Singleton on purpose: the whole value of TeamIntegrationState is remembering which mute states it
// has already logged, across the scoped lifetimes background jobs create per tick. Scoped would make
// it log every tick, which is the behavior it exists to prevent (#64).
builder.Services.AddSingleton<TeamIntegrationState>();

builder.Services.AddScoped<PaymentGenerationService>();
// Backs the public, unauthenticated youth-rate confirmation page (Pages/Public/YouthConfirm).
builder.Services.AddScoped<YouthPaymentConfirmationService>();
// Every Square value lives on Team (multi-team) — credentials, the environment they were issued
// for, and WebhookSignatureKey/WebhookNotificationUrl, each team verifying against its own key via
// the /webhooks/square/{teamId} route. There is no Square configuration section to bind.
builder.Services.AddScoped<SquareWebhookHandler>();
// Unmatched-order auto/manual matching + "complete the Square order once paid and the session's
// done" — used by SquareWebhookHandler above and by SessionActionService below.
builder.Services.AddScoped<SquarePaymentMatchingService>();
// Refunds are issued from the Web side only (a Session Manager clicking); the Worker just follows
// them to a conclusion, which is why RefundStatusService is registered there and this is not.
builder.Services.AddScoped<RefundService>();

builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<EmailTemplateRenderer>();
builder.Services.AddScoped<CandidateNotificationService>();

// Trigger points (#401). Registered in both hosts: the Worker runs the daily pass, and Web reaches
// the same engine through TeamPipeline's CandidateRegistered step when somebody presses Refresh.
builder.Services.AddScoped<IMessageTriggerScanner, CandidateRegisteredScanner>();
builder.Services.AddScoped<IMessageTriggerScanner, BeforeSessionStartScanner>();
builder.Services.AddScoped<IMessageTriggerScanner, FccFeeOutstandingScanner>();
builder.Services.AddScoped<IMessageTriggerScanner, PaymentUnpaidBeforeSessionScanner>();
// PR3's three, none of them seeded — a team opts in by creating a rule.
builder.Services.AddScoped<IMessageTriggerScanner, CandidateTestedScanner>();
builder.Services.AddScoped<IMessageTriggerScanner, LicenseGrantedScanner>();
builder.Services.AddScoped<IMessageTriggerScanner, FelonyDisclosureDeclaredScanner>();
builder.Services.AddScoped<MessageDispatchService>();
builder.Services.AddScoped<MessageRuleService>();
// Read by the Applicant Status page to colour its "days pending" column on the team's own
// boundaries rather than a constant (#401 PR2), and by the admin screen that sets them.
builder.Services.AddScoped<MessageThresholdService>();
builder.Services.AddScoped<MessageRuleAdminService>();

builder.Services.AddScoped<VecSubmissionService>();
// Reads and reports only — it never posts to ARRL. The submitting service is a separate
// registration (#197) so nothing can reach ARRL by resolving the thing that builds the preview.
builder.Services.AddScoped<ArrlSubmissionPreviewService>();
builder.Services.Configure<ArrlSubmissionOptions>(
    builder.Configuration.GetSection(ArrlSubmissionOptions.SectionName));
// The only thing in this app that talks to ARRL. Its URL is blank outside production, so a
// developer machine has nowhere to post rather than relying on nobody pressing the wrong button.
builder.Services.AddHttpClient<ArrlSubmissionClient>();
builder.Services.AddScoped<ArrlSubmissionArchiveStore>();
// The only service that causes a filing. Every call is irreversible, so nothing resolves this
// except the confirm handler on a screen showing exactly what would be sent.
builder.Services.AddScoped<ArrlSubmissionService>();

builder.Services.AddScoped<VolunteerExaminerReportService>();
builder.Services.AddScoped<SessionStatsService>();
builder.Services.AddScoped<VolunteerExaminerDirectoryService>();
builder.Services.AddScoped<VolunteerExaminerManagementService>();
builder.Services.AddScoped<DiscordTagSyncService>();
builder.Services.AddScoped<VolunteerExaminerMergeService>();
builder.Services.AddScoped<VolunteerExaminerImportService>();
builder.Services.AddScoped<VeSelfServiceLinkService>();
builder.Services.AddScoped<VeEmailChangeService>();
builder.Services.AddScoped<VeSessionInvitationService>();
// Writing to a team's VEs from the directory, and the CAN-SPAM unsubscribe behind it (#191).
builder.Services.AddScoped<VeMessageService>();
builder.Services.AddScoped<VeUnsubscribeService>();

// Pending-work counts shown as badges on the app nav (_AppLayout.cshtml); also the single source of
// the pending-VEC-submission predicate VecSubmissionReportService delegates to.
builder.Services.AddScoped<NavBadgeCountService>();

// The alert bell's feed (#339) — the same outstanding work, but carrying where to go and look at it.
builder.Services.AddScoped<AlertFeedService>();

// "Refresh candidates" button on the session detail page (Pages/SessionManager/Detail.cshtml.cs) —
// same per-team pipeline as the Worker's SessionIngestionJob, run on demand instead of waiting for
// its next tick. Registrations below mirror VeSessionManager.Worker/Program.cs's own (same
// singleton-vs-scoped reasoning in each comment there).
builder.Services.Configure<ExamToolsOptions>(builder.Configuration.GetSection(ExamToolsOptions.SectionName));
// ---------------------------------------------------------------------------------------------
// Some of the registrations below are resolved only by the Worker, and that is fine — see the same
// note in VeSessionManager.Worker/Program.cs for the reasoning (#360). Short version: both hosts
// build the same Core service graph, an unused scoped registration costs nothing at runtime, and a
// missing one fails at resolution time in production.
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton<IExamToolsClient, ExamToolsClient>();
builder.Services.AddScoped<SessionIngestionService>();
builder.Services.AddScoped<VolunteerExaminerSyncService>();
builder.Services.AddSingleton<IZoomClient, ZoomClient>();
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));
// One instance, two interfaces (#401 PR4). The bot login it caches is per-instance, so registering
// the class once and resolving both contracts from it is what stops a second login — and splitting
// the contracts keeps MessageDispatchService depending on the one call it makes.
builder.Services.AddSingleton<DiscordEventClient>();
builder.Services.AddSingleton<IDiscordEventClient>(sp => sp.GetRequiredService<DiscordEventClient>());
builder.Services.AddSingleton<IDiscordChannelMessageClient>(sp => sp.GetRequiredService<DiscordEventClient>());
builder.Services.AddSingleton<IDiscordGuildClient>(sp => sp.GetRequiredService<DiscordEventClient>());
builder.Services.AddScoped<SessionEventSchedulingService>();
builder.Services.AddScoped<JobRunHistoryLogger>();
// Backs Admin -> Job Schedule. Web-only: the Worker obeys the schedule, it has no need to report it.
builder.Services.AddScoped<JobScheduleService>();
// Resolved by ManualCandidateRefreshService since issue #81 — the manual pipeline now runs the
// exam-result step too, which is the escape hatch for a session graded after
// ExamResultSyncService.ResultSyncWindow has passed.
builder.Services.AddScoped<ExamResultSyncService>();
// The Renewal Monitor add flow resolves a call sign against ULS before saving, so a typo
// is rejected while the user is still on the page rather than becoming a row that
// silently never resolves. Same client the Worker's refresh job uses.
// Both hosts bind this, from appsettings.Shared.json (#302). Web registers the same lookup client
// the Worker's nightly sweep uses — RenewalMonitor's "Add license" calls it on the request path —
// so a value bound in only one host means the two can query different endpoints with no error, just
// quietly divergent data. Same reasoning that moved Jobs:* to the shared file.
builder.Services.Configure<UlsLookupOptions>(builder.Configuration.GetSection(UlsLookupOptions.SectionName));
builder.Services.AddSingleton<IUlsLookupClient, ExamToolsUlsLookupClient>();
builder.Services.AddScoped<LicenseWatchService>();
builder.Services.AddScoped<VolunteerExaminerLicenseWatchService>();
builder.Services.AddScoped<TeamPipeline>();
builder.Services.AddScoped<ManualCandidateRefreshService>();

// Issues #77/#73: Admin → Team Maintenance (team-level "Refresh now" + ingestion schedule
// visibility) and the site-wide Worker-health banner. IngestionScheduleService is the same gate the
// Worker uses, registered here so the UI's countdown is derived from it rather than restated.
// IngestionHealthCache is a SINGLETON on purpose (it caches across requests) and therefore resolves
// IngestionStatusService through a fresh scope rather than by injection — see its own remarks.
builder.Services.AddScoped<IngestionScheduleService>();
builder.Services.AddScoped<IngestionStatusService>();
builder.Services.AddScoped<TeamRefreshThrottle>();
// Web only ever QUEUES an import (HistoricalImportService.QueueAsync) — the Worker runs it.
builder.Services.AddScoped<HistoricalImportService>();
builder.Services.AddSingleton<IngestionHealthCache>();

// Same singleton reasoning as the line above, and for the same layout: the nav badges were four
// uncached COUNT queries on every authenticated page render (#291).
builder.Services.AddSingleton<NavBadgeCountCache>();
builder.Services.AddSingleton<AlertFeedCache>();

// Phase 9b: the actual UI-triggered wiring for every Session Manager action — see
// Pages/SessionManager/Detail.cshtml.cs.
builder.Services.AddScoped<CandidateActionService>();
builder.Services.AddScoped<SessionActionService>();

// Phase 9a: stateless, no DB dependency of its own — safe as a singleton.
builder.Services.AddSingleton<SessionAccessScope>();

// Self-service password reset (2026-08-01). Web only — the Worker has no login surface.
builder.Services.AddScoped<PasswordResetService>();

// Phase 9c: Admin Config Screens — SystemAdmin/TeamAdmin config surface (Pages/Admin/).
builder.Services.AddSingleton<AdminAccessScope>();
builder.Services.AddScoped<TeamSettingsService>();
builder.Services.AddScoped<TeamDeletionService>();
builder.Services.AddScoped<VecManagementService>();
builder.Services.AddScoped<FeeConfigurationService>();
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

// A SECOND, deliberately weak authentication scheme for volunteer examiners maintaining their own
// contact details (issue #142 phase 5). It exists alongside Identity and must never be mistaken for
// it.
//
// Three things keep them apart:
//
//   The scheme name. Every admin page authorises against the DEFAULT scheme (Identity), so a VE
//   cookie satisfies nothing there — the self-service page is the only one that names this scheme
//   explicitly.
//
//   The cookie path. Scoped to /VeSelfService, so the browser does not even send it to an admin
//   route. A bug that accepted any authenticated principal would still not see this cookie.
//
//   The claims. A VE principal carries an id and a name and NO role claim, so every
//   [Authorize(Roles = ...)] in the app fails closed for it rather than depending on the two rules
//   above holding.
builder.Services.AddAuthentication().AddCookie(VeSelfServiceAuth.Scheme, options =>
{
    options.Cookie.Name = "vesm_ve_self_service";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Path = "/VeSelfService";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;

    // Absolute, not sliding: this session is a convenience for one edit, and a link found later
    // should not be able to keep itself alive by being reloaded.
    options.ExpireTimeSpan = VeSelfServiceLinkService.SessionLifetime;
    options.SlidingExpiration = false;

    options.LoginPath = "/VeSelfService/SignIn";
    options.AccessDeniedPath = "/VeSelfService/SignIn";
});

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

    // Eight hours sliding, not the framework's fourteen-day default (#159). This is an admin backend
    // holding candidate PII; a cookie that survives a fortnight of inactivity on a shared or lost
    // machine is a long time to stay signed in. Sliding, so an actually-active session is never
    // interrupted mid-task — the window is about abandonment, not about forcing a daily re-login.
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // SameSite deliberately left at Identity's default (Lax) rather than raised to Strict.
    //
    // Lax already refuses to send this cookie on a cross-site POST, which is the CSRF case that
    // matters, and antiforgery tokens sit behind it regardless. Strict additionally drops the cookie
    // on a top-level *navigation* from another site — which is exactly how a user arrives back from
    // Google/Microsoft sign-in and from Square's hosted payment page. The visible result would be
    // "I was signed in a second ago and now I'm not", for no additional protection against the
    // attack Lax already covers.
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

// Rate limiting for the anonymous account endpoints (2026-08-03 hardening). Two distinct abuses
// this closes, both reachable by anyone on the internet once this app is public:
//   - Login: Identity's own lockout (5 tries) does NOT stop an attacker, it *helps* them — five
//     deliberate wrong passwords against a known admin address locks that account on a rolling
//     basis indefinitely. Capping requests per IP means the attacker's own rate is bounded first.
//   - Forgot password: PasswordResetService throttles per *user* (5 min), which does nothing
//     against breadth — a script with 10,000 addresses still triggers one real SMTP send per known
//     address, burning the deployment's mail quota and its sending-domain reputation.
//
// Deliberately a global limiter with an explicit no-limiter partition for everything else, rather
// than per-page [EnableRateLimiting] attributes: the protection is then on by default for any new
// page under /Account, which is the direction the mistake should fall.
//
// 20/minute is far above human use (a login is one GET + one POST) but low enough to make
// brute-force and mail-flooding useless. Static assets live outside /Account and are unaffected.
// HSTS: a year, and covering subdomains (L-02). The framework default is 30 days with neither
// includeSubDomains nor preload — defensible, but a short window means a user who has not visited
// recently is downgradeable again, and without includeSubDomains a future sibling host under the
// same domain is not covered at all.
//
// Deliberately NOT preload. Preload is a one-way door: removal takes months and is enforced by
// browsers rather than by this config, so it should not be switched on by a hygiene pass. Worth
// considering separately once the domain is settled.
//
// Note this only ever applies outside Development (see the UseHsts call below), so local http
// development is unaffected.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        // The bucket rule lives in RateLimitPolicy so it can be asserted (#264) — the Square webhook
        // matched no prefix here and fell through to "no limiter" for months, unnoticed, because a
        // prefix rule that defaults to Unlimited fails silently for anything new.
        //
        // /VeSelfService joins /Account in the Interactive bucket, and it is the more exposed of the
        // two: reachable with no account at all, it sends email on request, and behind it sits a
        // person's home address. The pages carry no per-page limiter attribute, deliberately, so a
        // new page under either prefix is covered the moment it exists.
        var bucket = RateLimitPolicy.For(context.Request.Path);
        if (bucket == RateLimitPolicy.Bucket.Unlimited)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        // Requires UseForwardedHeaders below to be effective behind the Apache reverse proxy —
        // without it every request would carry the proxy's own loopback address and the whole
        // internet would share one bucket. See the pipeline comment.
        var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var permitLimit = bucket == RateLimitPolicy.Bucket.Webhook
            ? RateLimitPolicy.WebhookPermitLimit
            : RateLimitPolicy.InteractivePermitLimit;

        // Partition key carries the bucket, so a webhook delivery and a login from the same address
        // cannot exhaust each other's allowance.
        return RateLimitPartition.GetFixedWindowLimiter($"{bucket}:{clientKey}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

// Authenticated by default; public is the exception and has to be asked for (audit T15, #158).
//
// Without this, a page added without [Authorize] is served to anyone, and nothing says so — no
// warning, no failing test, just a page that quietly needs no sign-in. Every page in this app
// except the fifteen marked [AllowAnonymous] holds candidate PII or admin controls, so the default
// was pointing the wrong way.
//
// Note this reaches minimal-API endpoints too, not only Razor Pages, which is why the Square
// webhook now says AllowAnonymous explicitly — without it, Square's deliveries would start
// returning 401 and the payment flow would fail silently on the *outside* of the app, where nothing
// here would log it.
//
// It reaches STATIC ASSETS as well: MapStaticAssets registers endpoints, so every CSS/JS/font
// request from a signed-out visitor was redirected to the login page until MapStaticAssets was given
// AllowAnonymous. See the note there — the pages still rendered, just unstyled, which is why a
// signed-in developer saw nothing wrong.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// No AddAntiforgery call here on purpose. The theme toggle is the app's only JavaScript-issued POST
// (Pages/Account/Theme.cshtml.cs) and sends its token as a "RequestVerificationToken" header rather
// than the hidden form field a <form> posts — which works untouched, because that is already
// AntiforgeryOptions.HeaderName's default. An AddAntiforgery(o => o.HeaderName = "…") line was
// written here first and removed once a mutation test showed it changed nothing: a no-op
// registration carrying a comment about why it is essential is worse than no line at all.
// ThemePreferenceTests covers both directions, so nulling or renaming HeaderName fails the build.

// Add services to the container.
// StaleAuthCookieFilter runs on every page: a cookie can outlive the account it names, and without
// it that lands as a 500 the person cannot act on. See the filter's own remarks.
builder.Services.AddScoped<StaleAuthCookieFilter>();
builder.Services.AddScoped<RememberFiltersPageFilter>();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new ServiceFilterAttribute(typeof(StaleAuthCookieFilter)));

    // Filters survive navigating away and back (#459). Registered globally but inert unless a
    // page model carries [RemembersFilters], so it costs one attribute check per request on
    // every page that does not opt in.
    options.Conventions.ConfigureFilter(new ServiceFilterAttribute(typeof(RememberFiltersPageFilter)));
});

var app = builder.Build();

// One-off bootstrap of the first SystemAdmin on a fresh deployment. Exits immediately instead of
// starting the web host — same shape as the Worker's --migrate-team-secrets/--run-uls switches.
// Must come before the normal startup path so it can run on a box where the service isn't up yet.
if (args.Contains(BootstrapAdminCommand.Switch))
{
    return await BootstrapAdminCommand.RunAsync(app.Services, args);
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // #443: both hosts migrate at startup, and only deploy.yml's start ordering used to keep them
    // apart — which does nothing on a reboot, on the HRCC box, or for a self-hoster. See MigrationLock.
    MigrationLock.Run(
        dbContext.Database.GetConnectionString() ?? string.Empty,
        message => startupLogger.LogInformation("{Message}", message),
        dbContext.Database.Migrate);

    // Fails the host rather than running with credentials it cannot read — see
    // DataProtectionKeyRingGuard for why that state is otherwise completely silent.
    await DataProtectionKeyRingGuard.VerifyAsync(dbContext, startupLogger);

    // Surfaces a credential that carries the Data Protection marker but will not decrypt (#160).
    // DataProtectionKeyRingGuard above catches that at startup; this covers anything that only
    // becomes readable-but-wrong later, and makes the fallback's silence visible either way.
    EncryptedStringConverter.OnUndecryptableValueRead ??= message => startupLogger.LogError("{Message}", message);


    if (app.Environment.IsDevelopment())
    {
        await DevAuthSeeder.SeedAsync(scope.ServiceProvider, startupLogger);
    }

    // No account is seeded on a fresh deployment — the first administrator is created explicitly
    // with --create-admin, so this app never ships a shared credential that works before setup.
    //
    // Refuse to serve at all in that state rather than starting into a login page that cannot
    // succeed: a running site whose every credential is rejected looks like a forgotten password or
    // a broken auth config, and is a worse thing to hand someone than a service that plainly did not
    // start. Exiting non-zero also makes the failure visible to systemd and to the deploy workflow,
    // which already waits on the unit becoming active.
    //
    // Guard is "can anyone sign in", not "does a user exist": the Worker's DevDataSeeder creates a
    // passwordless "System" user to own audit-trail foreign keys, so a row count would pass here on
    // a deployment nobody can actually get into.
    if (!await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AnyAsync(u => u.PasswordHash != null))
    {
        var refusal =
            "Refusing to start: no account on this deployment can sign in." + Environment.NewLine +
            $"  Create the first administrator with: dotnet VeSessionManager.Web.dll {BootstrapAdminCommand.Switch} --email <email> --name <name>" + Environment.NewLine +
            $"  A password is generated and printed; set {BootstrapAdminCommand.PasswordEnvironmentVariable} first to choose your own." + Environment.NewLine +
            "  The Worker is unaffected and can keep running.";

        // Written straight to stderr, not only through the logger. Returning here skips host
        // disposal, so Serilog never flushes and the message is lost from *both* the console and the
        // file sink — verified by running this against an empty database, where the process exited 1
        // with no explanation anywhere. That is precisely the confusing failure this check exists to
        // prevent, and on a systemd box (Restart=always) it would repeat silently every ten seconds.
        // stderr is captured by journalctl regardless of how Serilog is configured.
        Console.Error.WriteLine(refusal);
        startupLogger.LogCritical("{Refusal}", refusal);
        await Log.CloseAndFlushAsync();
        return 1;
    }
}

// Configure the HTTP request pipeline.

// Must be first: Production runs behind an Apache reverse proxy on the same box (see
// docs/deployment.md), so without this every request appears to come from loopback and carries
// Kestrel's own plain-HTTP scheme. Two things depend on it — the rate limiter's per-IP partition
// (which would otherwise put the entire internet in one bucket) and Request.Scheme. Defaults trust
// only loopback proxies, which is exactly the same-box topology here; no config needed, and in
// Development (no proxy, no X-Forwarded-* headers) this is a no-op.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Security response headers (2026-08-03 hardening) — there were none at all before this.
// frame-ancestors/X-Frame-Options is the load-bearing one: without it an attacker can frame an
// authenticated page and overlay a decoy button over a destructive action (clickjacking a Session
// Manager into deleting a candidate). The rest is defence in depth — with a CSP in place, a future
// encoding slip is contained instead of becoming a session-stealing XSS.
//
// Two allowances are deliberate and verified against the actual markup, not guesses:
//   - style-src 'unsafe-inline' + fonts.googleapis.com: both layouts load Google Fonts, and there
//     are ~139 inline style="" attributes across the pages. Removing those is the prerequisite for
//     tightening this, not something to do blind.
//   - font-src fonts.gstatic.com: what the Google Fonts stylesheet itself pulls.
// script-src stays 'self'. There is no inline JavaScript anywhere in Pages/ — and that is now
// enforced by InlineEventHandlerTests rather than asserted, because the failure mode is silent:
// an inline onchange= renders fine, reads correctly in the markup, and simply never runs. Two
// controls shipped dead that way before anyone noticed. Use app.js's data-autosubmit (or another
// delegated handler) instead.
// Same explicit-domain reasoning as the comment on "form-action" below, but derived from
// App:PublicBaseUrl (already the one config value every self-hosted deployment sets for its own
// domain — see ForgotPassword.cshtml.cs/VeSelfService for the other places this is read from,
// rather than the request's Host header) instead of a literal that would only ever be right for
// this one deployment.
var publicOrigin = new Uri(app.Configuration["App:PublicBaseUrl"]!, UriKind.Absolute).GetLeftPart(UriPartial.Authority);

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "same-origin";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "img-src 'self' data:; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        // Square is listed because the youth-rate flow hands the candidate off to Square-hosted
        // checkout; today that is a server-issued redirect rather than a cross-origin form post,
        // so 'self' alone would also pass — this keeps it correct if that ever becomes a direct post.
        //
        // accounts.google.com/login.microsoftonline.com are listed for a different reason, and are
        // NOT optional the way the comment above is (#185, live-caught 2026-08-27): the Login page's
        // "Sign in with Google"/"Sign in with Microsoft" buttons are real <form> posts to THIS origin
        // (self, already allowed), but Chrome enforces form-action against the server's subsequent
        // redirect target too, not just the form's own action URL — so the redirect Identity's
        // Challenge() issues to the provider's authorize endpoint was being silently blocked with no
        // error page, just a button that appeared to do nothing. Both buttons render conditionally
        // (see Program.cs's AddGoogle/AddMicrosoftAccount registration above), so listing both here
        // unconditionally costs nothing when a given provider isn't configured.
        //
        // {publicOrigin} is listed explicitly, redundantly with 'self' (2026-08-27) — a same-origin
        // form post was still being blocked by this exact directive with 'self' present and every
        // other layer (Apache, Cloudflare Managed Transforms/Transform Rules/Speed Brain, a Service
        // Worker, extensions, a meta-tag CSP) ruled out; adding the literal origin resolved it.
        // Derived from App:PublicBaseUrl rather than hardcoded so every self-hosted deployment gets
        // its own domain here automatically, the same as every other consumer of that config value.
        //
        // square.link AND *.square.site, NOT only *.squareup.com (2026-08-27, live-caught on the
        // youth-confirm page): the payment links Square's Create Payment Link API returns live on
        // https://square.link/u/…, which itself 303s to https://checkout.square.site/… — THREE
        // registrable domains for one checkout flow, and the *.squareup.com wildcard matches none of
        // the other two. Chrome enforces form-action on EVERY hop of a POST's redirect chain (the
        // same per-hop enforcement that required accounts.google.com above), so the youth-confirm
        // POST was blocked — with the console reporting the FORM's own (allowed, same-origin) URL as
        // the violating target, never the hop that actually failed. That misleading attribution cost
        // most of a day across Apache, Cloudflare, Service Workers, and extensions before curl
        // tracing the actual Location chain found it. If Square ever adds a fourth domain, this is
        // the line to extend.
        $"form-action 'self' {publicOrigin} https://*.squareup.com https://square.link https://*.square.site https://accounts.google.com https://login.microsoftonline.com";
    await next();
});

app.UseRouting();

app.UseRateLimiter();

// Was missing entirely before Phase 9a — UseAuthorization() alone never populated HttpContext.User,
// so it had been a silent no-op since Phase 0's scaffold.
app.UseAuthentication();
app.UseAuthorization();

// After authentication (it needs HttpContext.User) and after authorization (so an unauthorized
// request is still refused rather than redirected to a password form). Holds a user on Change
// password while an admin-chosen password is still in place — see RequirePasswordChangeMiddleware.
app.UseMiddleware<RequirePasswordChangeMiddleware>();

// ⚠️ AllowAnonymous is mandatory here, not tidy-up. MapStaticAssets registers real endpoints, so the
// FallbackPolicy above applies to them — which silently redirected every CSS, JS, font and image
// request to the login page for anyone not signed in. The HTML still returned 200, so the pages
// "worked": they just arrived with no styling and no scripts. Shipped in v0.3.0 and caught by a test
// that followed the script tags rather than trusting the page's status code.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages()
   .WithStaticAssets();
app.MapSquareWebhook();

app.Run();

// Required because the --create-admin branch above returns an exit code, which makes the
// top-level entry point int-returning.
return 0;

// Makes the top-level entry point reachable by WebApplicationFactory<Program> in
// VeSessionManager.Web.Tests, which boots this app in-process and renders every page. Top-level
// statements otherwise compile to an internal Program class the test assembly cannot name.
public partial class Program;
