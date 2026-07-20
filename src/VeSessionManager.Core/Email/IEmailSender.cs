namespace VeSessionManager.Core.Email;

/// <summary>
/// Sends a fully-composed email. Wrapped in an interface so notification logic can be unit
/// tested without live SMTP calls (per the spec's testing rules).
/// </summary>
public interface IEmailSender
{
    /// <summary>True once Email:SmtpHost is set. SMTP is effectively optional the same way Square is (see PaymentGenerationService) — an org that hasn't finished mail setup yet must not see a repeated failed-send error every poll; CandidateNotificationService checks this before attempting to send at all.</summary>
    bool IsConfigured { get; }

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
