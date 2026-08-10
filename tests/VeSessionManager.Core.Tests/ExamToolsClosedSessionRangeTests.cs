using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Ingestion;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// The closed-sessions date range, whose end bound ExamTools treats as <b>exclusive</b>.
///
/// <para><b>This cost months of silent data loss (found 2026-08-10).</b> The historical import
/// chunks by calendar month and passes an inclusive month-end, which went straight to an endpoint
/// that drops it — so <i>every chunk lost its final day</i>, roughly twelve days a year, for every
/// team and every range ever imported. Nothing failed: the request succeeded, the response was
/// valid, and the sessions simply did not exist afterwards. It surfaced only because a VE who had
/// worked on 31 May showed as inactive since the previous August, and a bot reading ExamTools
/// directly disagreed.</para>
///
/// <para>Verified against the live feed the same day: 2026-04-01..2026-04-30 returned 25 sessions
/// ending on the 29th, while 2026-04-01..2026-05-01 returned 27 — the two held on the 30th — and
/// still nothing from 1 May, confirming a strict <c>&lt;</c> rather than an off-by-one that merely
/// happened to help.</para>
/// </summary>
public class ExamToolsClosedSessionRangeTests
{
    /// <summary>The whole fix in one assertion: the caller's inclusive end date must reach ExamTools as the following day.</summary>
    [Fact]
    public void TheEndDateIsSentAsTheFollowingDayBecauseExamToolsExcludesIt()
    {
        var path = ExamToolsClient.ClosedSessionsPath("HRCC", new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));

        Assert.Contains("/api/veUser/sessions/2026-04-01/2026-05-01", path);
    }

    [Theory]
    [InlineData(2026, 4, 30, "2026-05-01")]   // month end
    [InlineData(2026, 5, 31, "2026-06-01")]   // month end, 31 days — the session Matt worked
    [InlineData(2026, 12, 31, "2027-01-01")]  // year end
    [InlineData(2028, 2, 29, "2028-03-01")]   // leap day
    [InlineData(2026, 6, 15, "2026-06-16")]   // ordinary mid-month day
    public void TheFollowingDayIsCorrectAcrossMonthYearAndLeapBoundaries(int year, int month, int day, string expected)
    {
        var path = ExamToolsClient.ClosedSessionsPath("HRCC", new DateOnly(year, month, 1), new DateOnly(year, month, day));

        Assert.Contains($"/{expected}?", path);
    }

    [Fact]
    public void TheTeamCodeIsEscaped()
    {
        var path = ExamToolsClient.ClosedSessionsPath("A TEAM/1", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.Contains("team=A%20TEAM%2F1", path);
    }

    /// <summary>
    /// The two halves have to agree. The import yields inclusive month-ends, and the client is what
    /// turns those into the exclusive bound ExamTools wants — so a chunk boundary must land on the
    /// first of the next month once it reaches the URL, with no day falling between two chunks.
    /// </summary>
    [Fact]
    public void EveryImportChunkCoversItsWholeMonthOnceItReachesTheUrl()
    {
        var chunks = HistoricalImportService.Chunks(new DateOnly(2026, 3, 15), new DateOnly(2026, 6, 10)).ToList();

        Assert.Equal(4, chunks.Count);

        // Each chunk's requested end is the day after its inclusive end...
        Assert.Contains("/2026-03-15/2026-04-01?", ExamToolsClient.ClosedSessionsPath("T", chunks[0].Start, chunks[0].End));
        Assert.Contains("/2026-04-01/2026-05-01?", ExamToolsClient.ClosedSessionsPath("T", chunks[1].Start, chunks[1].End));
        Assert.Contains("/2026-05-01/2026-06-01?", ExamToolsClient.ClosedSessionsPath("T", chunks[2].Start, chunks[2].End));
        Assert.Contains("/2026-06-01/2026-06-11?", ExamToolsClient.ClosedSessionsPath("T", chunks[3].Start, chunks[3].End));

        // ...and consecutive chunks meet exactly, so no day is covered twice or skipped.
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].End.AddDays(1), chunks[i].Start);
        }
    }
}
