using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Email;

/// <summary>
/// Seeds one EmailSettings row, the EmailTemplate rows, and (since #401) the four MessageRules that
/// reproduce this app's original automatic sends, per Team, if they don't already
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
                // DateTime.UtcNow rather than an injected TimeProvider, which every service here
                // uses (audit item D-16). Deliberate: this is a static startup seeder with no DI
                // scope of its own, and threading a clock through three static methods and two call
                // sites would buy a testable timestamp nothing asserts on — the seeder's tests are
                // about which rows appear, not when. If this ever becomes an instance service, take
                // the clock then.
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

        // #219: replaces PaymentReminder5Day, which chased the team's exam fee — money already
        // collected at the session, and so never actually outstanding when this fires. A new key
        // rather than new copy under the old one, because a deployment that customised the old
        // template must not have its text silently repurposed to a different fee. The old row is
        // simply no longer sent; see EmailTemplateTriggers.Retired.
        //
        // No payment link anywhere in this body, on purpose. FCC bills the applicant directly, and
        // the team's Square link pays a different bill.
        await SeedTemplateIfMissingAsync(dbContext, logger, team, "FccFeeReminder5Day",
            "Action needed: the FCC is still waiting for your application fee",
            """
            <p>Hi {{CandidateName}},</p>
            <p>Congratulations again on your exam on {{SessionDate}}. Your application has reached the
            FCC, and they are waiting on <strong>their</strong> application fee before your license can
            be issued.</p>
            <p><strong>This is the FCC's fee, not ours.</strong> You pay it directly to the FCC — we
            never handle it, and nothing is owed to us.</p>
            <p>Pay it at the FCC's CORES system:
            <a href="https://apps.fcc.gov/cores/userLogin.do">https://apps.fcc.gov/cores/userLogin.do</a></p>
            <p>You will need your FRN: <strong>{{Frn}}</strong></p>
            <p>The FCC emails a payment link to the address on your application when it is received, so
            it is worth checking your spam folder before starting over.</p>
            <p>If you have already paid, you can ignore this — it can take a few days for the FCC to
            record it.</p>
            """);

        await SeedTemplateIfMissingAsync(dbContext, logger, team, "PaymentExpirationNotice",
            "Unpaid Exam Fee Expired",
            """
            <p>{{CandidateName}}'s exam fee ({{PaymentAmount}}) from the session on {{SessionDate}} has gone
            10+ days without payment and is now marked expired.</p>
            <p>This is an internal notice — it goes to the Session Manager (EmailSettings.AdminNotificationEmail),
            not the candidate.</p>
            """);

        // Sent by a per-candidate button, NOT automatically (#221; this comment said otherwise until
        // #314/L-19). It used to fire from SessionActionService.MarkCompletedAsync for anyone whose
        // Tested flag that call flipped — which meant an email about someone's felony disclosure went
        // out as a side effect of a bulk status change, and could only ever arrive AFTER the exam,
        // when the candidate can no longer easily ask anyone about it. It is now offered whenever a
        // disclosure is declared, and Tested is not consulted at all. Informational only: the club
        // has no role beyond telling them extra FCC steps are required.
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
        // #144. Unlike every other template here, nothing sends this one: it is the starting text for
        // a message composed on a session's "Email candidates" screen, edited per send. The seeded
        // copy is therefore more obviously a placeholder than the others — what a team points new
        // licensees at is local knowledge this app cannot guess, and the whole point of storing it is
        // that they write it once and keep it.
        await SeedTemplateIfMissingAsync(dbContext, logger, team, "GettingStartedLocally",
            "Welcome to amateur radio — getting started locally",
            """
            <p>Hi {{CandidateFirstName}},</p>
            <p>Congratulations again on your exam on {{SessionDate}}. Here is how to get on the air
            with people near you.</p>
            <p><strong>Replace the list below with your own club's details before sending this.</strong></p>
            <ul>
              <li><strong>Club meetings</strong> — where and when</li>
              <li><strong>Nets</strong> — the weekly on-air check-in, with the frequency and time</li>
              <li><strong>Repeaters</strong> — the local machines and their offsets/tones</li>
              <li><strong>Who to ask</strong> — a name and an email for someone happy to help a new licensee</li>
            </ul>
            <p>Reply to this email if you have questions — someone here will answer.</p>
            <p>{{TeamName}}</p>
            """);

        await SeedTemplateIfMissingAsync(dbContext, logger, team, "ArrlYouthProgramInstructions",
            "ARRL Youth Program — Discount/Reimbursement Instructions",
            """
            <p>Hi {{CandidateName}},</p>
            <p>Congratulations on your new call sign, {{CallSign}}! As a young ham, you may be eligible
            for ARRL's youth discount/FCC-fee-reimbursement scholarship program.</p>
            <p>Details and the submission form are available from ARRL — reach out to us if you have
            questions about your eligibility.</p>
            """);

        await SeedMessageRulesAsync(dbContext, logger, team);
    }

    /// <summary>
    /// The four rules that reproduce what this app sent automatically before trigger points existed
    /// (#401) — 24 hours before a session, 5 days into an outstanding FCC fee, 10 days into an unpaid
    /// payment, and a confirmation on registration. Seeded per team so a team starts with today's
    /// behaviour and edits from there, exactly as the templates above are.
    ///
    /// <para><b><c>CreatedUtc</c> is the risky line, and it is deliberately "now".</b> Every scan is
    /// bounded by it, so a rule created at this moment never fires for a subject whose trigger moment
    /// already passed. On an existing deployment that means somebody who registered this morning and
    /// has not had their confirmation yet will not get one — accepted, and confirmed with Mike, as the
    /// price of the direction that cannot mass-mail. The migration that backfills a
    /// <c>MessageRuleRun</c> per already-sent message is the second, independent guard against the
    /// same thing; either alone would do, which is the point.</para>
    ///
    /// <para><b>Once per team, ever</b> — recorded by <see cref="Team.MessageRulesSeededUtc"/>, unlike
    /// the templates above which are checked row by row. That difference is load-bearing (#401 PR2):
    /// a per-trigger check re-adds a rule somebody deleted on the very next Worker start, quietly
    /// resuming a send they had stopped. Setting a new team up is a one-time act, not an invariant to
    /// maintain, and a team that wants no rule at a trigger point is entitled to have none.</para>
    /// </summary>
    private static async Task SeedMessageRulesAsync(AppDbContext dbContext, ILogger logger, Team team)
    {
        if (team.MessageRulesSeededUtc is not null)
        {
            return;
        }

        var createdUtc = DateTime.UtcNow;

        SeedRule(dbContext, logger, team, MessageTrigger.CandidateRegistered,
            "Registration confirmation", "RegistrationConfirmation", parameterHours: null, MessageRecipient.Candidate, createdUtc);

        SeedRule(dbContext, logger, team, MessageTrigger.BeforeSessionStart,
            "Reminder 24 hours before the session", "DayBeforeReminder", parameterHours: 24, MessageRecipient.Candidate, createdUtc);

        SeedRule(dbContext, logger, team, MessageTrigger.FccFeeOutstanding,
            "FCC fee reminder after 5 days", "FccFeeReminder5Day", parameterHours: 120, MessageRecipient.Candidate, createdUtc);

        // The one that never went to a candidate: it tells the Session Manager a payment link has
        // gone stale. That used to be a special case inside the send path; it is a field now.
        SeedRule(dbContext, logger, team, MessageTrigger.PaymentUnpaid,
            "Unpaid payment notice after 10 days", "PaymentExpirationNotice", parameterHours: 240, MessageRecipient.TeamAdminAddress, createdUtc);

        team.MessageRulesSeededUtc = createdUtc;
    }

    /// <summary>
    /// Seeds one example message, <b>switched off</b>.
    ///
    /// <para>Off is deliberate (Mike, 2026-08-21): these are examples of what a team can set up, not
    /// a set of emails a new team starts sending to real people without having read them. A team
    /// turns on the ones it wants.</para>
    ///
    /// <para>The words are copied from the template of the same name seeded moments earlier, so there
    /// is one source of the text while both models exist. That lookup goes away with the template
    /// table itself.</para>
    /// </summary>
    private static void SeedRule(
        AppDbContext dbContext, ILogger logger, Team team, MessageTrigger trigger, string name, string templateKey,
        int? parameterHours, MessageRecipient recipient, DateTime createdUtc)
    {
        var source = dbContext.EmailTemplates.Local.FirstOrDefault(t => t.TeamId == team.Id && t.Key == templateKey)
            ?? dbContext.EmailTemplates.FirstOrDefault(t => t.TeamId == team.Id && t.Key == templateKey);
        if (source is null)
        {
            logger.LogWarning("No seeded text for {TemplateKey}; skipping the example message for team {TeamId}", templateKey, team.Id);
            return;
        }

        dbContext.MessageRules.Add(new MessageRule
        {
            TeamId = team.Id,
            Name = name,
            Trigger = trigger,
            ParameterHours = parameterHours,
            Subject = source.Subject,
            Body = source.Body,
            Channel = MessageChannel.Email,
            Recipient = recipient,
            FanOut = MessageFanOut.PerRecipient,
            IsEnabled = false,
            CreatedUtc = createdUtc
        });
        logger.LogInformation("Seeded example message {Trigger} (\"{Name}\") for team {TeamId}, switched off", trigger, name, team.Id);
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
