namespace VeSessionManager.Core.Email;

public class EmailOptions
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;

    /// <summary>STARTTLS on connect, matching Mailgun's recommended port-587 setup.</summary>
    public bool UseStartTls { get; set; } = true;

    // SmtpUsername/SmtpPassword come from user-secrets or environment variables, never from appsettings files.
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
}
