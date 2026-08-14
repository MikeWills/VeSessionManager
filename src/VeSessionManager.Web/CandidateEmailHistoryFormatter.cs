using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Builds the "what has this candidate actually received" list shown by both the session Detail
/// page's "Email history" modal and the applicant detail page — every ...SentUtc field this app
/// tracks, in send order. See docs/email-reference.md. PaymentExpirationNotice is deliberately
/// excluded: it goes to the Session Manager's own inbox, not the candidate.
/// </summary>
public static class CandidateEmailHistoryFormatter
{
    public static IReadOnlyList<EmailHistoryLine> Build(Candidate candidate)
    {
        var lines = new List<EmailHistoryLine>();

        if (candidate.RegistrationConfirmationSentUtc is { } registrationSent)
        {
            lines.Add(new EmailHistoryLine("Registration email", FormatSentUtc(registrationSent)));
        }

        if (candidate.DayBeforeReminderSentUtc is { } dayBeforeSent)
        {
            lines.Add(new EmailHistoryLine("Reminder email", FormatSentUtc(dayBeforeSent)));
        }

        foreach (var payment in candidate.Payments.Where(p => p.PaymentReminderSentUtc is not null).OrderBy(p => p.PaymentReminderSentUtc))
        {
            var label = payment.Reason == PaymentReason.Retest ? "Payment reminder email (retest)" : "Payment reminder email";
            lines.Add(new EmailHistoryLine(label, FormatSentUtc(payment.PaymentReminderSentUtc!.Value)));
        }

        if (candidate.FelonyDisclosureInstructionsSentUtc is { } felonySent)
        {
            lines.Add(new EmailHistoryLine("Felony disclosure instructions email", FormatSentUtc(felonySent)));
        }

        if (candidate.YouthProgramInstructionsSentUtc is { } youthSent)
        {
            lines.Add(new EmailHistoryLine("Youth Program instructions email", FormatSentUtc(youthSent)));
        }

        return lines;
    }

    private static string FormatSentUtc(DateTime sentUtc) =>
        EasternTimeFormatter.Format(sentUtc, "M/d/yyyy h:mm tt");
}

public record EmailHistoryLine(string Label, string SentDisplay);
