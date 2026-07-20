namespace VeSessionManager.Core.Email;

/// <summary>Per-Team SMTP credentials — each team has its own separate SMTP account (confirmed with the user — not shared across teams). Unlike ExamTools/Zoom/Square, SmtpEmailSender needs no internal per-team cache (already stateless, connects fresh per send), so TeamId here is only for logging/traceability, not a cache key.</summary>
public sealed record EmailCredentials(int TeamId, string Host, int Port, string Username, string Password, bool UseStartTls);
