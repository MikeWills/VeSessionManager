using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Worker;

/// <summary>
/// Seeds the EmailSettings singleton row and the two EmailTemplate rows Phase 4 introduces, if
/// they don't already exist. Unlike DevDataSeeder, this runs in every environment (not just
/// Development) — real deployments need real template rows to send anything, not just local
/// dev convenience data. Idempotent per-row (checks existence individually) so it never
/// overwrites an Admin's edits to seeded content, matching the spec's "treat that content as a
/// starting point... not the source of truth going forward."
/// </summary>
public static class EmailDefaultsSeeder
{
    public static async Task SeedAsync(AppDbContext dbContext, ILogger logger)
    {
        if (!await dbContext.EmailSettings.AnyAsync())
        {
            dbContext.EmailSettings.Add(new EmailSettings
            {
                FromAddress = "noreply@example.org",
                FromDisplayName = "VE Session Manager",
                ReplyToAddress = "noreply@example.org",
                PrivacyPolicyUrl = "https://example.org/privacy",
                AdminNotificationEmail = "admin@example.org",
                UpdatedUtc = DateTime.UtcNow
            });
            logger.LogWarning("Seeded default EmailSettings with placeholder From/Reply-To/PrivacyPolicy/AdminNotification values — these must be updated before sending real candidate email");
        }

        // Deliberately demonstrates the formatting an Admin has available (headings, bold, a
        // bullet list, links) and every placeholder this template key supports — not meant to be
        // the final wording, just a real, edit-in-place starting point per the spec.
        await SeedTemplateIfMissingAsync(dbContext, logger, "RegistrationConfirmation",
            "Your VE Exam Session Registration",
            """
            <p>Hi {{CandidateFirstName}},</p>
            <p>You're registered for a Volunteer Examiner (VE) test session on <strong>{{SessionDate}}</strong>.</p>
            <p><a href="{{ZoomJoinUrl}}">Join the session on Zoom</a></p>
            <p><strong>Before the session:</strong></p>
            <ul>
              <li>Have a valid photo ID ready</li>
              <li>Have your FCC FRN number ready, if you have one</li>
              <li>Log in a few minutes early to test your camera and microphone</li>
            </ul>
            <p>Payment link (if applicable): {{PaymentLinkUrl}}</p>
            <p>Questions? Just reply to this email — {{CandidateFirstName}}, we're happy to help.</p>
            <p><a href="{{PrivacyPolicyUrl}}">Privacy Policy</a></p>
            """);

        await SeedTemplateIfMissingAsync(dbContext, logger, "DayBeforeReminder",
            "Reminder: Your VE Exam Session is Tomorrow",
            """
            <p>Hi {{CandidateFirstName}},</p>
            <p>This is a reminder that your VE test session is tomorrow, <strong>{{SessionDate}}</strong>.</p>
            <p><a href="{{ZoomJoinUrl}}">Join the session on Zoom</a></p>
            <ul>
              <li>Have a valid photo ID ready</li>
              <li>Log in a few minutes early to test your camera and microphone</li>
            </ul>
            <p>Outstanding payment link (if applicable): {{OutstandingPaymentLinkUrl}}</p>
            <p>See you then, {{CandidateFirstName}}!</p>
            """);

        await SeedTemplateIfMissingAsync(dbContext, logger, "PaymentReminder5Day",
            "Reminder: Your VE Exam Fee is Still Due",
            """
            <p>Hi {{CandidateName}},</p>
            <p>Your FCC application has been received, but we haven't seen your exam fee payment yet.</p>
            <p><a href="{{PaymentLinkUrl}}">Pay your exam fee</a></p>
            <p><a href="{{ZoomJoinUrl}}">Session Zoom link</a> (for reference)</p>
            <p>Thanks!</p>
            """);

        await SeedTemplateIfMissingAsync(dbContext, logger, "PaymentExpirationNotice",
            "Unpaid Exam Fee Expired",
            """
            <p>{{CandidateName}}'s exam fee ({{PaymentAmount}}) from the session on {{SessionDate}} has gone
            10+ days without payment and is now marked expired.</p>
            <p>This is an internal notice — it goes to the Session Manager (EmailSettings.AdminNotificationEmail),
            not the candidate.</p>
            """);

        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedTemplateIfMissingAsync(AppDbContext dbContext, ILogger logger, string key, string subject, string body)
    {
        if (await dbContext.EmailTemplates.AnyAsync(t => t.Key == key))
        {
            return;
        }

        dbContext.EmailTemplates.Add(new EmailTemplate { Key = key, Subject = subject, Body = body });
        logger.LogInformation("Seeded default EmailTemplate {Key}", key);
    }
}
