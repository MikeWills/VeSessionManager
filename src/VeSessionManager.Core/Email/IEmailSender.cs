namespace VeSessionManager.Core.Email;

/// <summary>
/// Sends a fully-composed email. Wrapped in an interface so notification logic can be unit
/// tested without live SMTP calls (per the spec's testing rules).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailCredentials credentials, EmailMessage message, CancellationToken cancellationToken);
}
