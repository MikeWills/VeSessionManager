using VeSessionManager.Web.Pages.Admin;
using Xunit;

namespace VeSessionManager.Web.Tests;

/// <summary>
/// Which calendar month the Reconciliation page's "Re-import" button covers.
///
/// <para>Reported live 2026-08-15: four April findings whose button appeared to do nothing. The
/// direct cause was elsewhere (findings that age out of the sweep window were never re-examined, so
/// they could not resolve), but the same investigation found this — the month was derived from the
/// session's <b>UTC</b> date while the page displayed the Eastern one.</para>
///
/// <para>For a mid-month session the two agree and nothing is visibly wrong. On the last evening of a
/// month they do not: 2026-04-30 21:00 ET is 2026-05-01 01:00 UTC, so the button would read
/// "Re-import May 2026", import a month that does not contain the session, and leave the finding open
/// however many times it was pressed. Silent, and indistinguishable from the bug above.</para>
/// </summary>
public class ReconciliationImportMonthTests
{
    /// <summary>The failing case: an evening session on the last day of April, stored as May 1st in UTC.</summary>
    [Fact]
    public void AnEveningSessionOnTheLastOfTheMonth_ImportsItsOwnMonth_NotTheNextOne()
    {
        // 2026-05-01 01:00 UTC == 2026-04-30 21:00 ET.
        var (start, end) = ReconciliationModel.ImportMonthFor(new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 4, 1), start);
        Assert.Equal(new DateOnly(2026, 4, 30), end);
    }

    /// <summary>The mirror case at a year boundary, where being a month out is also being a year out.</summary>
    [Fact]
    public void AnEveningSessionOnNewYearsEve_ImportsDecember_NotJanuary()
    {
        // 2027-01-01 02:00 UTC == 2026-12-31 21:00 ET.
        var (start, end) = ReconciliationModel.ImportMonthFor(new DateTime(2027, 1, 1, 2, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 12, 1), start);
        Assert.Equal(new DateOnly(2026, 12, 31), end);
    }

    /// <summary>A mid-month session, where UTC and Eastern agree — the case that made the bug invisible.</summary>
    [Fact]
    public void AMidMonthSession_SpansItsWholeMonth()
    {
        var (start, end) = ReconciliationModel.ImportMonthFor(new DateTime(2026, 4, 16, 1, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 4, 1), start);
        Assert.Equal(new DateOnly(2026, 4, 30), end);
    }

    /// <summary>February, so the end date is not assumed to be the 30th or 31st.</summary>
    [Fact]
    public void FebruaryEndsOnItsOwnLastDay()
    {
        var (start, end) = ReconciliationModel.ImportMonthFor(new DateTime(2026, 2, 10, 17, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 2, 1), start);
        Assert.Equal(new DateOnly(2026, 2, 28), end);
    }
}
