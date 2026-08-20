using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Uls;

/// <summary>
/// Reconciles a candidate's stored application timeline against what the ULS lookup just returned
/// (#195).
///
/// <para><b>Reconcile, do not rewrite.</b> The obvious implementation — clear the collection and
/// re-add everything each run — is correct and wrong: the watcher visits every open candidate on a
/// schedule, and Web and Worker share one SQLite file with a single writer. Rewriting an unchanged
/// timeline would add write churn carrying no information, on precisely the contended path #434
/// exists to measure. So this returns whether anything actually differs, and the caller saves only
/// then — the same shape as the <c>FccHoldReason</c>/<c>FccPaymentStatus</c> checks beside it.</para>
///
/// <para><b>The lookup is the authority.</b> An entry FCC no longer reports is removed rather than
/// kept, following the same "the feed is truth" rule the rest of this app applies to ExamTools.
/// Keeping a retracted action would leave a Session Manager reading something FCC has withdrawn.</para>
/// </summary>
public static class UlsTimeline
{
    /// <summary>
    /// Brings <paramref name="candidate"/>'s stored timeline in line with <paramref name="entries"/>,
    /// returning whether anything changed. Requires <c>candidate.UlsHistory</c> to be loaded.
    /// </summary>
    public static bool Reconcile(Candidate candidate, IReadOnlyList<UlsHistoryEntry> entries)
    {
        // Chronological, because a timeline is read top to bottom and the endpoint's own ordering is
        // not guaranteed. Undated entries sort first rather than being dropped — a missing date is
        // not a reason to hide that the action happened.
        var incoming = entries
            .OrderBy(e => e.LogDateUtc ?? DateTime.MinValue)
            .ThenBy(e => e.Code, StringComparer.Ordinal)
            .ToList();

        if (Matches(candidate.UlsHistory, incoming))
        {
            return false;
        }

        candidate.UlsHistory.Clear();
        foreach (var entry in incoming)
        {
            candidate.UlsHistory.Add(new CandidateUlsHistoryEntry
            {
                LogDateUtc = entry.LogDateUtc,
                Code = entry.Code,
                CodeText = entry.CodeText
            });
        }

        return true;
    }

    /// <summary>
    /// Order-sensitive on purpose: the stored rows are written in order, so a difference in sequence
    /// is a difference worth writing. Compares the text too — it can arrive on a later poll for an
    /// entry already stored, and an entry that gains its words is a real improvement to show.
    /// </summary>
    private static bool Matches(ICollection<CandidateUlsHistoryEntry> stored, List<UlsHistoryEntry> incoming)
    {
        if (stored.Count != incoming.Count)
        {
            return false;
        }

        return stored
            .OrderBy(e => e.LogDateUtc ?? DateTime.MinValue)
            .ThenBy(e => e.Code, StringComparer.Ordinal)
            .Zip(incoming)
            .All(pair => pair.First.LogDateUtc == pair.Second.LogDateUtc
                      && pair.First.Code == pair.Second.Code
                      && pair.First.CodeText == pair.Second.CodeText);
    }
}
