using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issues #258 and #259 — two instances of one hole: an admin-supplied <b>connection endpoint</b>
/// stored with no validation, which the app then authenticates to using credentials that admin may
/// never have known.
///
/// <para><b>Why it is a real escalation and not just a config mistake.</b> Secrets here are
/// deliberately write-only — <c>TeamSettingsService</c>'s own class comment says pages never echo a
/// stored secret back, only a masked placeholder. So a successor TeamAdmin, or a compromised
/// TeamAdmin session, cannot read the ExamTools or SMTP password. They could, however, repoint the
/// <i>host</i> and leave the password field blank (which means "keep existing"), and the next poll
/// would post the stored credentials to a server they control. For SMTP it is worse: they also
/// receive a copy of every candidate email.</para>
///
/// <para>The same primitive reaches inside the network — <c>http://127.0.0.1</c>,
/// <c>169.254.169.254</c> — which is ordinary SSRF, and a malformed value was an unhandled
/// <c>UriFormatException</c> thrown from a background job rather than from the page that accepted
/// it.</para>
/// </summary>
public class TeamEndpointValidationTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TeamSettingsService CreateService(AppDbContext dbContext) =>
        new(dbContext, new FixedTimeProvider(Now), NullLogger<TeamSettingsService>.Instance);

    private static async Task<(Team Team, User User)> SeedAsync(AppDbContext dbContext)
    {
        var user = new User { Name = "Admin", Role = UserRole.TeamAdmin };
        var team = new Team
        {
            Name = "HRCC",
            CreatedUtc = Now,
            ExamToolsTeamCode = "HRCC",
            ExamToolsUsername = "ve@example.org",
            ExamToolsPassword = "stored-secret",
            ExamToolsBaseUrl = "https://exam.tools",
            SmtpHost = "smtp.mailgun.org",
            SmtpUsername = "postmaster@example.org",
            SmtpPassword = "stored-smtp-secret"
        };
        dbContext.Users.Add(user);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        return (team, user);
    }

    // ---- #258: ExamTools base URL -----------------------------------------------------------

    [Theory]
    [InlineData("https://attacker.example")]            // the credential-exfiltration case
    [InlineData("http://exam.tools")]                   // right host, no TLS
    [InlineData("http://127.0.0.1:8080")]               // SSRF, loopback
    [InlineData("http://169.254.169.254/latest/meta-data/")] // SSRF, cloud metadata
    [InlineData("https://exam.tools.attacker.example")] // suffix-match trap
    [InlineData("not a url")]                           // the UriFormatException case
    [InlineData("//exam.tools")]                        // scheme-relative, not absolute
    public async Task UpdateExamTools_RejectsAnUnacceptableBaseUrl_AndLeavesTheStoredOneIntact(string baseUrl)
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).UpdateExamToolsAsync(
            team.Id, "HRCC", "ve@example.org", password: null, baseUrl, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.InvalidExamToolsBaseUrl, result);

        // The stored value must survive a rejected edit — otherwise a failed attempt still breaks
        // ingestion, which is its own denial of service.
        var reloaded = await dbContext.Teams.AsNoTracking().SingleAsync();
        Assert.Equal("https://exam.tools", reloaded.ExamToolsBaseUrl);
        Assert.Equal("stored-secret", reloaded.ExamToolsPassword);
    }

    [Theory]
    [InlineData("https://exam.tools")]
    [InlineData("https://alpha.exam.tools")]
    [InlineData("https://examtools.dev")]
    [InlineData("https://exam.tools/")]                 // trailing slash
    [InlineData("HTTPS://EXAM.TOOLS")]                  // case
    public async Task UpdateExamTools_AcceptsAKnownExamToolsHost(string baseUrl)
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).UpdateExamToolsAsync(
            team.Id, "HRCC", "ve@example.org", password: null, baseUrl, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
    }

    /// <summary>
    /// Blank means "use the deployment default", which is the normal state for every team — it must
    /// not be caught by the new validation.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateExamTools_BlankBaseUrl_ClearsTheOverrideRatherThanFailing(string? baseUrl)
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).UpdateExamToolsAsync(
            team.Id, "HRCC", "ve@example.org", password: null, baseUrl, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        Assert.Null((await dbContext.Teams.AsNoTracking().SingleAsync()).ExamToolsBaseUrl);
    }

    // ---- #259: SMTP host --------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("172.16.4.2")]
    [InlineData("smtp host with spaces")]
    [InlineData("http://smtp.example.org")]   // a URL, not a host
    public async Task UpdateSmtp_RejectsAnUnacceptableHost_AndLeavesTheStoredOneIntact(string host)
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).UpdateSmtpAsync(
            team.Id, host, 587, "postmaster@example.org", password: null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.InvalidSmtpHost, result);

        var reloaded = await dbContext.Teams.AsNoTracking().SingleAsync();
        Assert.Equal("smtp.mailgun.org", reloaded.SmtpHost);
        Assert.Equal("stored-smtp-secret", reloaded.SmtpPassword);
    }

    [Theory]
    [InlineData("smtp.mailgun.org")]
    [InlineData("smtp.gmail.com")]
    [InlineData("mail.example.co.uk")]
    public async Task UpdateSmtp_AcceptsAnOrdinaryPublicHost(string host)
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).UpdateSmtpAsync(
            team.Id, host, 587, "postmaster@example.org", password: null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
    }

    /// <summary>Clearing SMTP is how a team turns email off; it must stay possible.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UpdateSmtp_BlankHost_ClearsTheSettingRatherThanFailing(string? host)
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);

        var result = await CreateService(dbContext).UpdateSmtpAsync(
            team.Id, host, null, null, password: null, user.Id, CancellationToken.None);

        Assert.Equal(TeamActionResult.Success, result);
        Assert.Null((await dbContext.Teams.AsNoTracking().SingleAsync()).SmtpHost);
    }

    /// <summary>
    /// A rejected edit must not write an audit row either — otherwise the trail says a credential
    /// change happened when none did.
    /// </summary>
    [Fact]
    public async Task ARejectedEdit_WritesNoAuditEntry()
    {
        await using var dbContext = CreateContext();
        var (team, user) = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        await service.UpdateExamToolsAsync(team.Id, "HRCC", "ve@example.org", null, "https://attacker.example", user.Id, CancellationToken.None);
        await service.UpdateSmtpAsync(team.Id, "127.0.0.1", 587, "u", null, user.Id, CancellationToken.None);

        Assert.Empty(await dbContext.AuditLogs.ToListAsync());
    }
}
