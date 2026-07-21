using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Square;
using VeSessionManager.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton(TimeProvider.System);
// No SquareOptions here — WebhookSignatureKey/WebhookNotificationUrl now live on Team (multi-team,
// each team verifies against its own key via the /webhooks/square/{teamId} route). Nothing else in
// this project needs SquareOptions:Environment.
builder.Services.AddScoped<SquareWebhookHandler>();

// Phase 9a: stateless, no DB dependency of its own — safe as a singleton. Not wired into a real
// data page yet (that's Phase 9b); registered now so it's ready.
builder.Services.AddSingleton<SessionAccessScope>();

// AddIdentityCore, not AddIdentity — deliberately skips Identity's own Role tables (Role stays one
// plain enum column on User, see docs/admin-auth.md). AddIdentityCookies() (below) supplies the
// ApplicationScheme/ExternalScheme cookie schemes that AddIdentity would otherwise add for you.
builder.Services.AddIdentityCore<User>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false; // no email-confirmation infra exists yet
        options.Password.RequiredLength = 10;
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
