using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Builds the "what has this candidate actually received" list shown by both the session Detail
/// page's "Email history" modal and the applicant detail page, in send order.
///
/// <para><b>Rule-driven mail comes from <c>MessageRuleRun</c> now, not from <c>Candidate.*SentUtc</c>
/// (#415).</b> The columns were a fixed set of app-defined names, which is the opposite of what #401
/// is for, and they failed in three separate ways: a rule on a trigger with no column sent mail that
/// never appeared here; two rules on one trigger collapsed into one line; and the FCC fee reminder
/// stamps a column this list never read, so it never showed at all. The run carries the rule's own
/// name, which is more use than "Reminder email" — it says *which* rule.</para>
///
/// <para><b>What still comes from a column, and why.</b> Three sends are not rule-driven, so no run
/// exists for them: the Youth Program instructions and the payment reminder (whose column has no
/// writer left at all — it is purely historical), and the felony instructions when sent by hand.
/// That last one is written by <i>both</i> the on-demand button and
/// <c>FelonyDisclosureDeclaredScanner</c>, so it is shown only when no run covers it; otherwise the
/// run's line would be joined by a second, worse-labelled duplicate.</para>
///
/// <para><c>PaymentUnpaid</c> needs no exclusion here: its subject is the <i>payment</i>, not the
/// candidate, so it never matches this candidate's runs. That is structural rather than a filter
/// somebody has to remember — see <c>CandidateRuleSends</c> for the ones that do need filtering.</para>
/// </summary>
public static class CandidateEmailHistoryFormatter
{
    public static IReadOnlyList<EmailHistoryLine> Build(Candidate candidate, IReadOnlyList<RuleSend> ruleSends)
    {
        var lines = new List<(string Label, DateTime SentUtc)>();

        foreach (var send in ruleSends)
        {
            lines.Add((send.Label, send.SentUtc));
        }

        // No writer remains for this column — the FCC fee reminder became a rule in #401 and stamps
        // Candidate.FccFeeReminderSentUtc instead. Kept because rows written before that are recorded
        // nowhere else, and dropping the line would quietly erase them from the page.
        foreach (var payment in candidate.Payments.Where(p => p.PaymentReminderSentUtc is not null))
        {
            var label = payment.Reason == PaymentReason.Retest ? "Payment reminder email (retest)" : "Payment reminder email";
            lines.Add((label, payment.PaymentReminderSentUtc!.Value));
        }

        if (candidate.FelonyDisclosureInstructionsSentUtc is { } felonySent
            && !ruleSends.Any(s => s.Trigger == MessageTrigger.FelonyDisclosureDeclared))
        {
            lines.Add(("Felony disclosure instructions email", felonySent));
        }

        // No trigger sends this one, so a rule can never cover it.
        if (candidate.YouthProgramInstructionsSentUtc is { } youthSent)
        {
            lines.Add(("Youth Program instructions email", youthSent));
        }

        // Hand-composed sends (#144) — rows rather than columns, because a team writes its own
        // templates and a column per template cannot be added by somebody at runtime. Requires
        // candidate.EmailSends to be Included by the caller, same as Payments above.
        foreach (var send in candidate.EmailSends)
        {
            lines.Add((send.TemplateLabel, send.SentUtc));
        }

        return [.. lines
            .OrderBy(l => l.SentUtc)
            .Select(l => new EmailHistoryLine(l.Label, FormatSentUtc(l.SentUtc)))];
    }

    private static string FormatSentUtc(DateTime sentUtc) =>
        EasternTimeFormatter.Format(sentUtc, "M/d/yyyy h:mm tt");
}

public record EmailHistoryLine(string Label, string SentDisplay);
