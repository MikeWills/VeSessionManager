using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #195, persistence half. The watcher already fetches this on every run, so the timeline is
/// stored rather than re-fetched at render time — the issue's own note that this "costs no
/// additional polling" only holds if the page reads what the poll already had.
/// </summary>
public class UlsTimelinePersistenceTests
{
    private static UlsHistoryEntry Entry(int day, string code, string? text = null)
        => new(new DateTime(2026, 8, day), code, text);

    /// <summary>Baseline: entries arrive and are kept, newest information intact.</summary>
    [Fact]
    public void EntriesFromALookup_AreStoredAgainstTheCandidate()
    {
        var candidate = new Candidate();

        var changed = UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF", "Redlight Review Initiated"), Entry(4, "RDLCOM", "Redlight Review Completed")]);

        Assert.True(changed);
        Assert.Collection(candidate.UlsHistory,
            first => Assert.Equal("Redlight Review Initiated", first.CodeText),
            second => Assert.Equal("Redlight Review Completed", second.CodeText));
    }

    /// <summary>
    /// The property that matters most on this deployment. The watcher runs against every open
    /// candidate on a schedule, and Web and Worker share one SQLite file with a single writer — so a
    /// reconcile that rewrote an unchanged timeline every run would add write churn for no
    /// information, on exactly the contended path #434 instrumented.
    /// </summary>
    [Fact]
    public void AnUnchangedTimeline_IsNotRewritten()
    {
        var candidate = new Candidate();
        UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF", "Redlight Review Initiated")]);
        var stored = candidate.UlsHistory.Single();

        var changed = UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF", "Redlight Review Initiated")]);

        Assert.False(changed);
        Assert.Same(stored, candidate.UlsHistory.Single());  // same instance: nothing was replaced
    }

    /// <summary>A new FCC action is the whole point — it must land.</summary>
    [Fact]
    public void ANewEntry_IsPickedUp()
    {
        var candidate = new Candidate();
        UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF", "Redlight Review Initiated")]);

        var changed = UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF", "Redlight Review Initiated"), Entry(4, "RDLCOM", "Redlight Review Completed")]);

        Assert.True(changed);
        Assert.Equal(2, candidate.UlsHistory.Count);
    }

    /// <summary>
    /// FCC's mirror is the authority, so a timeline it no longer reports stops being shown —
    /// the same "the feed is truth" rule the rest of the app follows. Anything else would leave a
    /// Session Manager reading an action FCC has retracted.
    /// </summary>
    [Fact]
    public void EntriesTheLookupNoLongerReports_AreDropped()
    {
        var candidate = new Candidate();
        UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF"), Entry(4, "RDLCOM")]);

        var changed = UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF")]);

        Assert.True(changed);
        Assert.Equal("RDLOFF", candidate.UlsHistory.Single().Code);
    }

    /// <summary>Text arriving later for an entry already stored is still a change worth writing.</summary>
    [Fact]
    public void TextAppearingOnAnExistingEntry_CountsAsAChange()
    {
        var candidate = new Candidate();
        UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF")]);

        var changed = UlsTimeline.Reconcile(candidate, [Entry(1, "RDLOFF", "Redlight Review Initiated")]);

        Assert.True(changed);
        Assert.Equal("Redlight Review Initiated", candidate.UlsHistory.Single().CodeText);
    }

    /// <summary>Oldest first — a timeline read top-to-bottom is the point, and the endpoint's own order is not guaranteed.</summary>
    [Fact]
    public void TheStoredTimeline_IsChronological()
    {
        var candidate = new Candidate();

        UlsTimeline.Reconcile(candidate, [Entry(9, "RDLCOM"), Entry(2, "RDLOFF"), Entry(5, "BQOFF")]);

        Assert.Equal(["RDLOFF", "BQOFF", "RDLCOM"], candidate.UlsHistory.Select(e => e.Code));
    }

    /// <summary>An application with no history at all is normal, not an error.</summary>
    [Fact]
    public void NoEntries_IsNotAChange_WhenThereWereNone()
        => Assert.False(UlsTimeline.Reconcile(new Candidate(), []));
}
