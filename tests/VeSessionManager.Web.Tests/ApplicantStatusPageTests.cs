using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// The "Pending FCC grant" count beside the section heading — how many candidates are being watched,
/// without scrolling the table and counting.
///
/// <para>What these tests actually pin is that the number and the table cannot disagree. A count
/// rendered from a different query than the rows beneath it is the failure worth guarding against:
/// it stays plausible while it is wrong, and the page gives the reader no way to notice. So each
/// test asserts the heading number <i>and</i> the row count, from one rendered response.</para>
/// </summary>
public class ApplicantStatusPageTests : IClassFixture<WebAppFactory>
{
    private const string Url = "/SessionManager/ApplicantStatus";

    private readonly WebAppFactory _factory;

    public ApplicantStatusPageTests(WebAppFactory factory) => _factory = factory;

    /// <summary>
    /// Replaces the seeded candidates with <paramref name="pendingCount"/> candidates that match the
    /// page's own Pending filter — <c>Tested</c> and awaiting the FCC (Unmatched or Received) — plus
    /// two that deliberately do not, so a count that ignored the filter would be visibly wrong
    /// rather than accidentally right.
    /// </summary>
    private async Task SeedPendingAsync(int pendingCount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Candidates.RemoveRange(await db.Candidates.ToListAsync());
        await db.SaveChangesAsync();

        for (var i = 0; i < pendingCount; i++)
        {
            db.Candidates.Add(new Candidate
            {
                SessionId = _factory.Seeded.SessionId,
                Name = $"Pending Candidate {i}",
                Email = $"pending{i}@localhost",
                DateRegisteredUtc = DateTime.UtcNow.AddDays(-14),
                Tested = true,
                ApplicationStatus = i % 2 == 0
                    ? CandidateApplicationStatus.Unmatched
                    : CandidateApplicationStatus.Received
            });
        }

        // Already granted — the whole point of the section is that these drop off.
        db.Candidates.Add(new Candidate
        {
            SessionId = _factory.Seeded.SessionId,
            Name = "Already Granted",
            Email = "granted@localhost",
            DateRegisteredUtc = DateTime.UtcNow.AddDays(-14),
            Tested = true,
            ApplicationStatus = CandidateApplicationStatus.Granted
        });

        // Registered but never sat the exam — nothing is pending for them at the FCC.
        db.Candidates.Add(new Candidate
        {
            SessionId = _factory.Seeded.SessionId,
            Name = "Never Tested",
            Email = "nottested@localhost",
            DateRegisteredUtc = DateTime.UtcNow.AddDays(-14),
            Tested = false,
            ApplicationStatus = CandidateApplicationStatus.Unmatched
        });

        await db.SaveChangesAsync();
    }

    /// <summary>The number rendered in the pill beside the "Pending FCC grant" heading.</summary>
    private static string HeadingCount(string html) =>
        Regex.Match(html, """Pending FCC grant<span class="pill-count[^"]*">(\d+)</span>""").Groups[1].Value;

    /// <summary>Data rows in the pending table — the empty-state row carries a colspan, so it is not one.</summary>
    private static int PendingRowCount(string html)
    {
        var table = Regex.Match(html, """<table class="cards" data-sortable="pending-fcc-grant">.*?</table>""", RegexOptions.Singleline).Value;
        Assert.NotEmpty(table);

        var body = Regex.Match(table, "<tbody>.*?</tbody>", RegexOptions.Singleline).Value;
        return Regex.Matches(body, "<tr>").Count - Regex.Matches(body, "colspan=").Count;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TheHeadingCountMatchesTheRowsBeneathIt(int pendingCount)
    {
        await SeedPendingAsync(pendingCount);
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var response = await client.GetAsync(Url);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(pendingCount.ToString(), HeadingCount(html));
        Assert.Equal(pendingCount, PendingRowCount(html));
    }

    /// <summary>
    /// Zero renders as a muted "0" rather than disappearing. An absent number reads as "this did not
    /// load"; a visible 0 answers the question. Same reasoning as the team picker's pills, which is
    /// why it reuses their <c>zero</c> class.
    /// </summary>
    [Fact]
    public async Task NothingPending_ShowsAMutedZeroRatherThanNoCountAtAll()
    {
        await SeedPendingAsync(0);
        using var client = _factory.CreateClientAs(UserRole.SessionManager);

        var response = await client.GetAsync(Url);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", HeadingCount(html));
        Assert.Contains("""Pending FCC grant<span class="pill-count zero">""", html);
        Assert.Contains("Nobody is currently waiting on an FCC grant.", html);
    }

    /// <summary>
    /// The application-received date is shown as the date it is, <b>not</b> converted to Eastern.
    ///
    /// <para>Every FCC date arrives date-only and is stamped at UTC midnight by
    /// <c>ExamToolsUlsLookupClient.AsUtcDate</c>, so it already is a wall-clock date. Running it
    /// through <c>EasternTimeFormatter</c> — which is correct for the session date in the next column,
    /// a real instant — renders 8pm the day before, so every application would read as received a day
    /// early. A day is exactly the size of error nobody spots on a page about elapsed days.</para>
    /// </summary>
    [Fact]
    public async Task ApplicationReceivedDate_IsNotShiftedByTimezoneConversion()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Candidates.RemoveRange(await db.Candidates.ToListAsync());
            db.Candidates.Add(new Candidate
            {
                SessionId = _factory.Seeded.SessionId,
                Name = "Awaiting Grant",
                Email = "awaiting@localhost",
                DateRegisteredUtc = DateTime.UtcNow.AddDays(-20),
                Tested = true,
                ApplicationStatus = CandidateApplicationStatus.Received,
                // Exactly how the ULS client stamps it: date-only, midnight UTC.
                ApplicationDateEnteredUtc = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        var html = await _factory.CreateClientAs(UserRole.SystemAdmin).GetStringAsync(Url);

        Assert.Contains("Application received", html);
        Assert.Contains("Aug 13, 2026", html);
        // The Eastern conversion would render the 12th.
        Assert.DoesNotContain("Aug 12, 2026", html);
    }

    /// <summary>Still Unmatched means FCC has nothing on file, so there is no date to show — the same "—" the days column uses, for the same reason.</summary>
    [Fact]
    public async Task ApplicationReceivedDate_IsBlankWhileTheFccHasNothingOnFile()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Candidates.RemoveRange(await db.Candidates.ToListAsync());
            db.Candidates.Add(new Candidate
            {
                SessionId = _factory.Seeded.SessionId,
                Name = "Unmatched Candidate",
                Email = "unmatched@localhost",
                DateRegisteredUtc = DateTime.UtcNow.AddDays(-20),
                Tested = true,
                ApplicationStatus = CandidateApplicationStatus.Unmatched,
                ApplicationDateEnteredUtc = null
            });
            await db.SaveChangesAsync();
        }

        var html = await _factory.CreateClientAs(UserRole.SystemAdmin).GetStringAsync(Url);

        Assert.Contains("Unmatched Candidate", html);
        Assert.Contains("Application received", html);
    }

}
