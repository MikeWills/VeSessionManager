using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Email;

/// <summary>Per-Team SMTP credentials — each team has its own separate SMTP account (confirmed with the user — not shared across teams). Unlike ExamTools/Zoom/Square, SmtpEmailSender needs no internal per-team cache (already stateless, connects fresh per send), so TeamId here is only for logging/traceability, not a cache key.</summary>
public sealed record EmailCredentials(int TeamId, string Host, int Port, string Username, string Password, bool UseStartTls);

/// <summary>
/// Single definition of the Team -> EmailCredentials mapping, including the port 587 / StartTLS
/// true fallbacks used when a team hasn't set those optional fields explicitly — previously
/// re-typed identically at every call site in CandidateNotificationService/PaymentReminderService,
/// risking the fallback silently drifting between them.
/// </summary>
public static class TeamEmailCredentialsExtensions
{
    public static EmailCredentials ToEmailCredentials(this Team team) =>
        new(team.Id, team.SmtpHost!, team.SmtpPort ?? 587, team.SmtpUsername!, team.SmtpPassword ?? "", team.SmtpUseStartTls ?? true);
}
