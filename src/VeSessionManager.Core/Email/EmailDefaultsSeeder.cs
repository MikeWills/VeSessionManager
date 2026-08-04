using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Seeds one EmailSettings row and the four EmailTemplate rows per Team, if they don't already
/// exist for that team. Unlike DevDataSeeder, this runs in every environment (not just
/// Development) — real deployments need real template rows to send anything, not just local
/// dev convenience data. Idempotent per-row (checks existence individually, per team) so it never
/// overwrites an Admin's edits to seeded content, matching the spec's "treat that content as a
/// starting point... not the source of truth going forward."
///
/// Multi-team: both EmailSettings and EmailTemplate content are per-team (confirmed with the
/// user — templates are customizable per team, not shared) — this loops every Team and seeds a
/// full set for each, rather than seeding once globally. See docs/multi-team.md.
///
/// **Two callers, and the important one is team creation** (moved here from the Worker project
/// 2026-08-04). This used to run *only* at Worker startup, over whichever teams existed at that
/// moment — so a team created afterwards through Admin → Teams had no templates and, worse, no
/// EmailSettings row, which made CandidateNotificationService skip that team entirely with one log
/// line and send nothing at all. The Web process could create a team that only a restart of a
/// different process could make functional, and nothing said so. TeamSettingsService.CreateAsync now
/// calls SeedForTeamAsync directly; the Worker's startup sweep stays as an idempotent backfill for
/// teams that predate this (and for any created while the Worker was down).
/// </summary>
public static class EmailDefaultsSeeder
{
    public static async Task SeedAsync(AppDbContext dbContext, ILogger logger)
    {
        var teams = await dbContext.Teams.ToListAsync();
        foreach (var team in teams)
        {
            await SeedForTeamAsync(dbContext, logger, team);
        }

        await dbContext.SaveChangesAsync();
    }

    public static async Task SeedForTeamAsync(AppDbContext dbContext, ILogger logger, Team team)
    {
        if (!await dbContext.EmailSettings.AnyAsync(e => e.TeamId == team.Id))
        {
            dbContext.EmailSettings.Add(new EmailSettings
            {
                TeamId = team.Id,
                FromAddress = "noreply@example.org",
                FromDisplayName = "VE Session Manager",
                ReplyToAddress = "noreply@example.org",
                PrivacyPolicyUrl = "https://example.org/privacy",
                AdminNotificationEmail = "admin@example.org",
                UpdatedUtc = DateTime.UtcNow
            });
            logger.LogWarning("Seeded default EmailSettings for team {TeamId} ({TeamName}) with placeholder From/Reply-To/PrivacyPolicy/AdminNotification values — these must be updated before sending real candidate email",
                team.Id, team.Name);
        }

        // Deliberately demonstrates the formatting an Admin has available (headings, bold, a
        // bullet list, links) and every placeholder this template key supports — not meant to be
        // the final wording, just a real, edit-in-place starting point per the spec.
        await SeedTemplateIfMissingAsync(dbContext, logger, team, "RegistrationConfirmation",
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

        await SeedTemplateIfMissingAsync(dbContext, logger, team, "DayBeforeReminder",
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

        await SeedTemplateIfMissingAsync(dbContext, logger, team, "PaymentReminder5Day",
            "Reminder: Your VE Exam Fee is Still Due",
            """
            <p>Hi {{CandidateName}},</p>
            <p>Your FCC application has been received, but we haven't seen your exam fee payment yet.</p>
            <p><a href="{{PaymentLinkUrl}}">Pay your exam fee</a></p>
            <p><a href="{{ZoomJoinUrl}}">Session Zoom link</a> (for reference)</p>
            <p>Thanks!</p>
            """);

        await SeedTemplateIfMissingAsync(dbContext, logger, team, "PaymentExpirationNotice",
            "Unpaid Exam Fee Expired",
            """
            <p>{{CandidateName}}'s exam fee ({{PaymentAmount}}) from the session on {{SessionDate}} has gone
            10+ days without payment and is now marked expired.</p>
            <p>This is an internal notice — it goes to the Session Manager (EmailSettings.AdminNotificationEmail),
            not the candidate.</p>
            """);

        // Phase 9b: sent automatically by SessionActionService.MarkCompletedAsync for a candidate
        // whose Tested flag just flipped true and who has HasFelonyDisclosure = true — informational
        // only, the club has no role beyond telling them special FCC steps are required.
        await SeedTemplateIfMissingAsync(dbContext, logger, team, "FelonyDisclosureInstructions",
            "Important: Additional FCC Steps Required",
            """
            <p>Hi {{CandidateName}},</p>
            <p>Because you disclosed a felony conviction on your exam application, the FCC requires an
            additional step before your license can be granted: you must submit an explanation of the
            circumstances directly to the FCC as part of your application.</p>
            <p>This is an FCC requirement, not something our club administers — we can't advise on the
            content of your submission, only let you know it's required.</p>
            <p>Questions about the process itself should go to the FCC directly.</p>
            """);

        // Phase 9b: manual, per-candidate trigger from the Session Manager ("Send ARRL Youth Program
        // instructions"), only surfaced when the session's Vec.SupportsYouthProgram = true.
        await SeedTemplateIfMissingAsync(dbContext, logger, team, "ArrlYouthProgramInstructions",
            "ARRL Youth Program — Discount/Reimbursement Instructions",
            """
            <p>Hi {{CandidateName}},</p>
            <p>Congratulations on your new call sign, {{CallSign}}! As a young ham, you may be eligible
            for ARRL's youth discount/FCC-fee-reimbursement scholarship program.</p>
            <p>Details and the submission form are available from ARRL — reach out to us if you have
            questions about your eligibility.</p>
            """);
    }

    private static async Task SeedTemplateIfMissingAsync(AppDbContext dbContext, ILogger logger, Team team, string key, string subject, string body)
    {
        if (await dbContext.EmailTemplates.AnyAsync(t => t.TeamId == team.Id && t.Key == key))
        {
            return;
        }

        dbContext.EmailTemplates.Add(new EmailTemplate { TeamId = team.Id, Key = key, Subject = subject, Body = body });
        logger.LogInformation("Seeded default EmailTemplate {Key} for team {TeamId}", key, team.Id);
    }
}
