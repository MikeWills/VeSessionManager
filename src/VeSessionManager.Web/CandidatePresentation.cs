using System.Globalization;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// How a candidate is described to a human, in the places both the session roster and the candidate
/// page need to agree.
///
/// <para><b>What prompted it (audit T28):</b> the same status rendered as <c>"Not tested"</c> on a
/// candidate's own record and as <c>"Withdrew/no-show"</c> in the "other attempts" list on that same
/// page. Someone comparing two of their own attempts saw two different words for one thing.</para>
///
/// <para><b>What is deliberately NOT here: the FRN line.</b> The two pages really do want different
/// text — the roster renders it inline and needs the <c>"FRN "</c> prefix to be legible, while the
/// candidate page renders it under a field label that already says FRN, where repeating it reads
/// badly. That is a contextual difference, not drift, and folding it in would make one of the two
/// worse. The audit listed it as duplication; it isn't.</para>
/// </summary>
public static class CandidatePresentation
{
    /// <summary>One row of the FCC application timeline (#195): when, what FCC called it, and the raw code.</summary>
    public record TimelineLine(string DateLine, string Description, string Code);

    /// <summary>
    /// The candidate's FCC application timeline, ready to render.
    ///
    /// <para><b>Why this exists on screen at all:</b> <c>wireless2.fcc.gov</c> returns Akamai
    /// "Access Denied" to this deployment, so <c>FccUlsLinks</c> deliberately ships no application
    /// deep link — there is nowhere to send a Session Manager who asks what is happening with an
    /// application. These entries come from the lookup the watcher already makes, and say more than
    /// FCC's own page would.</para>
    ///
    /// <para>⚠️ <b>The date is not run through <c>EasternTimeFormatter</c></b>, matching the two FCC
    /// date fields beside it on the same page. These are date-only values stored at UTC midnight;
    /// converting one to Eastern shifts it back a calendar day and misreports the date FCC gave.</para>
    /// </summary>
    public static IReadOnlyList<TimelineLine> ApplicationTimeline(IEnumerable<CandidateUlsHistoryEntry> entries)
        => entries
            .Select(e => new TimelineLine(
                e.LogDateUtc?.ToString("M/d/yyyy", CultureInfo.InvariantCulture) ?? "—",
                // Same fallback as UlsHistoryEntry.Description, and for the same reason: code_text is
                // observed on an undocumented endpoint rather than specified, so a row that loses its
                // words must still show the action rather than render blank.
                string.IsNullOrWhiteSpace(e.CodeText) ? e.Code : e.CodeText.Trim(),
                e.Code))
            .ToList();

    /// <summary>
    /// The label for a candidate's application status.
    ///
    /// <para>Only <see cref="CandidateApplicationStatus.NotTested"/> needs translating — the rest
    /// read correctly as their own names. "Not tested" is used rather than "Withdrew/no-show"
    /// because it matches the enum and was already the majority spelling; **if that wording should
    /// change, this is now the one line to change.** That, rather than the wording itself, is the
    /// point of this method.</para>
    /// </summary>
    public static string StatusLabel(CandidateApplicationStatus status) =>
        status == CandidateApplicationStatus.NotTested ? "Not tested" : status.ToString();

    /// <summary>
    /// The candidate's name, or the stand-in shown once their PII has been cleared.
    ///
    /// <para>A withdrawn candidate's row is kept for statistics but their personal details are
    /// purged, so there is genuinely nothing to show — see <see cref="Candidate.IsWithdrawn"/> and
    /// docs/pii-purge.md.</para>
    /// </summary>
    public static string DisplayName(Candidate candidate) =>
        candidate.IsWithdrawn ? "Withdrew — PII cleared" : candidate.Name ?? "—";
}
