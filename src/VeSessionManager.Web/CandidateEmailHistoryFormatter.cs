using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Builds the "what has this candidate actually received" list shown by both the session Detail
/// page's "Email history" modal and the applicant detail page, in send order.
///
/// <para><b>Two sources, and no special cases (#417).</b> Everything the message engine sent is a
/// <c>MessageRuleRun</c> — including hand-sends, since #417 made a button just another kind of
/// trigger — and everything composed by hand on the "Email candidates" screen is a
/// <c>CandidateEmailSend</c>. There is nothing else to consult.</para>
///
/// <para><b>It used to read the legacy <c>Candidate.*SentUtc</c> columns</b> (#415), which is a fixed
/// set of app-defined names — the opposite of what #401 is for. It failed three ways: a rule on a
/// trigger with no column sent invisible mail; two rules on one trigger collapsed into one line; and
/// the FCC fee reminder stamps a column this never read, so it never appeared at all. Reading runs
/// fixed those, but left per-column fallbacks and a dedup rule behind, because the hand-sends were
/// still recorded only in columns. #417 removed the reason for them, and the backfill migration
/// covers the rows written before it.</para>
///
/// <para><c>PaymentUnpaid</c> needs no exclusion: its subject is the <i>payment</i>, not the
/// candidate, so it never matches. Structural, rather than a filter somebody has to remember. Which
/// runs count as received is decided once, in <see cref="CandidateRuleSends"/>.</para>
/// </summary>
public static class CandidateEmailHistoryFormatter
{
    public static IReadOnlyList<EmailHistoryLine> Build(Candidate candidate, IReadOnlyList<RuleSend> ruleSends)
    {
        var lines = ruleSends.Select(s => (s.Label, s.SentUtc)).ToList();

        // Hand-composed sends (#144) — rows rather than columns, because a team writes its own
        // templates and a column per template cannot be added by somebody at runtime. Deliberately
        // still its own table: this one sends *edited* text rather than a template render, so it is
        // not the engine's kind of message. Requires candidate.EmailSends to be Included by the caller.
        lines.AddRange(candidate.EmailSends.Select(send => (send.TemplateLabel, send.SentUtc)));

        return [.. lines
            .OrderBy(l => l.SentUtc)
            .Select(l => new EmailHistoryLine(l.Label, FormatSentUtc(l.SentUtc)))];
    }

    private static string FormatSentUtc(DateTime sentUtc) =>
        EasternTimeFormatter.Format(sentUtc, "M/d/yyyy h:mm tt");
}

public record EmailHistoryLine(string Label, string SentDisplay);
