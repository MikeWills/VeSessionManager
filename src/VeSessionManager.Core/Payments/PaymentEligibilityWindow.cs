namespace VeSessionManager.Core.Payments;

/// <summary>
/// How far back a session's scheduled start may be before this app stops doing anything
/// money-related for its candidates — creating exam payments, generating Square links, sending
/// reminders, expiring unpaid links.
///
/// **Why this exists (2026-08-01).** The one-time historical import backfilled a year of real
/// sessions. `PaymentGenerationService` had no age bound at all — only `Session.Status == Active`,
/// which per CLAUDE.md means "not cancelled", never "not finished" — so it created ~1710 Unpaid
/// `InitialExam` payments for candidates who tested months ago. They sat harmless only because that
/// team had no Square credentials; the moment Square was configured, the very next poll would have
/// generated ~1710 real payment links for people who tested last winter, and `PaymentReminderService`
/// would then have emailed them about it. Found in the Worker log before that happened.
///
/// **Why a window and not `HasEnded`.** "Has this session ended?" is the wrong test for money: a
/// payment reminder keys off `Candidate.ApplicationDateEnteredUtc`, which FCC sets *after* the
/// session runs, so reminders legitimately target sessions that already ended. Bounding by age keeps
/// that working while excluding backfilled history — the same shape as
/// `ExamResultSyncService.ResultSyncWindow`, and for the same reason.
///
/// **Why 30 days.** It must clear the longest real path from session to a live payment concern:
/// application entered some days after the session, then the team's `PaymentUnpaid` threshold —
/// 240 hours by default, and a team's own to set since #401 — before an unpaid link expires. 30 leaves
/// comfortable headroom without reaching into imported history, whose nearest rows are months old.
///
/// **Which is a bound this window does not enforce**: a team that pushes its unpaid-payment rule past
/// 30 days will find these passes stop seeing the payment before its own threshold arrives. Nothing
/// warns about that today; the admin form's ceiling is a year.
///
/// Anchored on `Session.ScheduledStartUtc` — deliberately **not** `ExamToolsClosedUtc`, which the
/// historical import stamps at *import* time and would therefore make every backfilled session look
/// like it closed today. That is the same trap the exam-result window documents.
/// </summary>
public static class PaymentEligibilityWindow
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>The earliest <c>ScheduledStartUtc</c> still in scope for payment work.</summary>
    public static DateTime CutoffUtc(DateTime nowUtc) => nowUtc - Window;
}
