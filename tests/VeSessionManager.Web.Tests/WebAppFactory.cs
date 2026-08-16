using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Boots the real Web app in-process against a throwaway database, with authentication faked so a
/// page can be requested as any role.
///
/// <para><b>Why this exists.</b> Nothing else in this repo renders Razor. Two bugs reached a
/// deployment in a single day because of that: a form carrying both <c>action=</c> and
/// <c>asp-page-handler</c> (which <c>FormTagHelper</c> throws on, at render time, so the build was
/// clean and every test passed), and an anchor where <c>asp-all-route-data</c> silently discarded
/// <c>asp-route-id</c> so every link to a VE pointed at nobody. A page that is never rendered
/// anywhere before it reaches a browser is not tested, however green the suite is.</para>
///
/// <para><b>The fake authentication is half the value.</b> Every interesting page is
/// <c>[Authorize]</c>d, so before this the only way to see one was for a human to log in and click.
/// A header-driven scheme means a test — or an agent — can exercise them unattended.</para>
/// </summary>
public class WebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>See the registration in ConfigureTestServices for why this exists.</summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress ??= System.Net.IPAddress.Loopback;
                await nextMiddleware();
            });
            next(app);
        };
    }

    /// <summary>Sent by the client to choose who the request is from. Read only by the test auth handler, which exists only in this assembly.</summary>
    public const string RoleHeader = "X-Test-Role";

    public const string TestScheme = "IntegrationTest";

    /// <summary>Forges a principal for a user id that was never seeded — see CreateClientWithStaleCookie.</summary>
    private const string StaleUserHeader = "X-Test-Stale-User";

    /// <summary>Held open for the lifetime of the factory: a SQLite in-memory database exists only while a connection to it does.</summary>
    private SqliteConnection? _connection;

    /// <summary>
    /// Every email the app tried to send during this factory's life.
    ///
    /// <para>The registration below replaces <c>SmtpEmailSender</c>, which is not optional: a page
    /// test that triggers a send would otherwise open a real socket to whatever host the seeded team
    /// carries, and fail slowly on a DNS timeout rather than quickly on an assertion. Nothing here
    /// should be talking to a mail server.</para>
    /// </summary>
    public List<VeSessionManager.Core.Email.EmailMessage> SentEmails { get; } = [];

    private sealed class CapturingEmailSender(List<VeSessionManager.Core.Email.EmailMessage> sent)
        : VeSessionManager.Core.Email.IEmailSender
    {
        public Task SendAsync(
            VeSessionManager.Core.Email.EmailCredentials credentials,
            VeSessionManager.Core.Email.EmailMessage message,
            CancellationToken cancellationToken)
        {
            sent.Add(message);
            return Task.CompletedTask;
        }
    }

    public SeededIds Seeded { get; private set; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // NOT "Development": that branch runs DevAuthSeeder against whatever database is configured,
        // and NOT "Production"/"Test" either, since both carry real connection strings and key-ring
        // paths from their own appsettings. An environment with no appsettings file of its own falls
        // back to the base file, and the registrations below replace the parts that matter.
        builder.UseEnvironment("IntegrationTest");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Keeps the app's own AddDataProtection call away from the repo's real key ring.
                ["DataProtection:KeyRingPath"] = Path.Combine(Path.GetTempPath(), "vesm-tests", Guid.NewGuid().ToString()),
                ["App:PublicBaseUrl"] = "http://localhost"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            // Replace the real registration wholesale. Program.cs calls Database.Migrate() during
            // startup, so this must be in place before the host runs — ConfigureTestServices is
            // applied after the app's own registrations and before the host is built, which is
            // exactly the window needed. A pleasant side effect: every migration is exercised on
            // every run.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            // Migrate and seed HERE, before the host starts, because Program.cs refuses to start at
            // all when no account can sign in:
            //
            //     "Refusing to start: no account on this deployment can sign in."
            //
            // That guard is right — a running site whose every credential is rejected is worse than
            // one that plainly did not start — so the harness satisfies it rather than switching it
            // off for tests. Weakening a startup safety check to make a test pass would be testing a
            // configuration nobody runs.
            //
            // Migrating here also means the app's own Migrate() call finds everything applied and
            // no-ops, and every migration gets exercised on every test run.
            Seed(_connection);

            // The app authenticates with Identity cookies. Re-declaring the default scheme here
            // points [Authorize] at the handler below instead; nothing about the app's own schemes
            // is removed, so the real ones still exist and are simply not the default.
            services.AddAuthentication(TestScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestScheme, _ => { });

            // The limiter partitions on the client IP, and every test request shares one. It only
            // covers /Account and /VeSelfService today, but a crawl of those would otherwise trip
            // the 20/minute bucket and produce 429s that look like page failures.
            services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options =>
                options.GlobalLimiter = null);

            // TestServer leaves Connection.RemoteIpAddress null — there is no real socket behind it.
            // Anything reading the client address therefore sees null in tests and a real value in
            // production, which is the wrong way round for a security feature: the audit log's
            // source address (#265) and the per-IP rate limiter both depend on it. Setting a
            // loopback address makes the harness resemble the deployment rather than a case that
            // never occurs.
            services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter());

            // See SentEmails: no test may open a socket to a mail server.
            services.RemoveAll<VeSessionManager.Core.Email.IEmailSender>();
            services.AddSingleton<VeSessionManager.Core.Email.IEmailSender>(new CapturingEmailSender(SentEmails));

            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        });
    }

    /// <summary>A client whose every request is authenticated as <paramref name="role"/>.</summary>
    public HttpClient CreateClientAs(UserRole role)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(RoleHeader, role.ToString());
        return client;
    }

    /// <summary>
    /// A client whose principal is authenticated and well-formed but names a user id that does not
    /// exist — the state a browser is left in when an account is deleted, or the database is
    /// restored, beneath a still-valid cookie.
    ///
    /// <para>It cannot be produced by deleting the seeded row: this harness's auth handler looks the
    /// user up itself, so a missing row just yields an anonymous request, which exercises the
    /// authorization challenge rather than the stale-cookie path.</para>
    /// </summary>
    public HttpClient CreateClientWithStaleCookie()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(RoleHeader, UserRole.SystemAdmin.ToString());
        client.DefaultRequestHeaders.Add(StaleUserHeader, "true");
        return client;
    }

    /// <summary>
    /// The minimum real data every page needs to render something rather than an empty state — an
    /// empty database would let a page "pass" by rendering nothing, which is the failure mode this
    /// whole harness exists to avoid.
    /// </summary>
    private void Seed(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(options);
        db.Database.Migrate();

        // PasswordHash must be non-null: Program.cs's startup guard asks whether anyone *can sign
        // in*, not whether a row exists, precisely because the Worker seeds a passwordless "System"
        // user to own audit foreign keys.
        var user = new User
        {
            Name = "Test Admin",
            Email = "admin@localhost",
            Role = UserRole.SystemAdmin,
            PasswordHash = "not-a-real-hash-never-used-the-test-scheme-authenticates"
        };
        var vec = new Vec { Name = "ARRL" };
        var team = new Team { Name = "TEST-TEAM", ExamToolsTeamCode = "TEST" };
        db.Users.Add(user);
        db.Vecs.Add(vec);
        db.Teams.Add(team);
        db.SaveChanges();

        db.UserTeams.Add(new UserTeam { UserId = user.Id, TeamId = team.Id });

        var fee = new FeeConfiguration
        {
            VecId = vec.Id,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            CreatedByUserId = user.Id,
            CreatedUtc = DateTime.UtcNow
        };
        db.FeeConfigurations.Add(fee);
        db.SaveChanges();

        var session = new Session
        {
            TeamId = team.Id,
            VecId = vec.Id,
            FeeConfigurationId = fee.Id,
            ExamToolsSessionId = "et-test-session",
            Title = "Smoke test session",
            ScheduledStartUtc = DateTime.UtcNow.AddDays(-7),
            DurationMinutes = 60,
            Status = SessionStatus.Active,
            ExamToolsClosedUtc = DateTime.UtcNow.AddDays(-7).AddHours(3)
        };
        db.Sessions.Add(session);

        var person = new VolunteerExaminer { Name = "Test VE", CallSign = "N0TEST" };
        db.VolunteerExaminers.Add(person);
        db.SaveChanges();

        db.VeTeamMemberships.Add(new VeTeamMembership { VolunteerExaminerId = person.Id, TeamId = team.Id, IsActive = true });
        db.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { SessionId = session.Id, VolunteerExaminerId = person.Id });

        var candidate = new Candidate
        {
            SessionId = session.Id,
            Name = "Test Candidate",
            Email = "candidate@localhost",
            DateRegisteredUtc = DateTime.UtcNow.AddDays(-14)
        };
        db.Candidates.Add(candidate);
        db.SaveChanges();

        Seeded = new SeededIds
        {
            UserId = user.Id,
            TeamId = team.Id,
            VecId = vec.Id,
            SessionId = session.Id,
            CandidateId = candidate.Id,
            VolunteerExaminerId = person.Id
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection?.Dispose();
        }
    }

    public class SeededIds
    {
        public int UserId { get; init; }
        public int TeamId { get; init; }
        public int VecId { get; init; }
        public int SessionId { get; init; }
        public int CandidateId { get; init; }
        public int VolunteerExaminerId { get; init; }
    }

    /// <summary>
    /// Authenticates every request as the seeded admin, with whatever role the request header asks
    /// for.
    ///
    /// <para>The NameIdentifier claim must be the real seeded user's id: pages resolve the current
    /// user through <c>GetUserWithManagerAsync</c>, which parses that claim and loads the row. A
    /// principal with a role but no matching row would 403 or throw, and the failure would look
    /// like a page bug rather than a harness bug.</para>
    /// </summary>
    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IServiceScopeFactory scopeFactory)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var requested))
            {
                // No header means anonymous, so the harness can also assert that a page correctly
                // refuses an unauthenticated visitor.
                return AuthenticateResult.NoResult();
            }

            if (Request.Headers.ContainsKey(StaleUserHeader))
            {
                // Deliberately an id no seeded row has. Everything else about the principal is
                // correct — signed, authenticated, carrying a role — which is exactly what makes
                // this state hard to spot in production.
                var staleIdentity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "999999"),
                    new Claim(ClaimTypes.Name, "deleted@example.org"),
                    new Claim(ClaimTypes.Role, UserRole.SystemAdmin.ToString())
                ], TestScheme);

                return AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(staleIdentity), TestScheme));
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstOrDefaultAsync();
            if (user is null)
            {
                return AuthenticateResult.Fail("No seeded user — call SeedAsync first.");
            }

            var role = requested.ToString();
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Email ?? "test"),
                new Claim(ClaimTypes.Role, role)
            ], TestScheme);

            return AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme));
        }
    }
}
