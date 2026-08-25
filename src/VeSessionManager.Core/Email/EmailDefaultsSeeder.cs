using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Email;

/// <summary>
/// What a team starts with: one <see cref="EmailSettings"/> row and a set of example
/// <see cref="MessageRule"/>s, per team. Runs in every environment, unlike DevDataSeeder — a real
/// deployment needs these rows to send anything at all.
///
/// <para><b>Templates are gone (2026-08-21).</b> This used to seed seven <c>EmailTemplate</c> rows and
/// then four rules pointing at four of them by key. A message owns its words now, so there is one
/// pass and one table: the same seven pieces of text, each on the trigger that sends it.</para>
///
/// <para><b>Two callers, and the important one is team creation</b> (moved here from the Worker
/// project 2026-08-04). This used to run <i>only</i> at Worker startup, over whichever teams existed
/// at that moment — so a team created afterwards through Admin → Teams had no EmailSettings row,
/// which made CandidateNotificationService skip that team entirely with one log line and send
/// nothing. The Web process could create a team that only a restart of a different process could
/// make functional, and nothing said so. <c>TeamSettingsService.CreateAsync</c> now calls
/// <see cref="SeedForTeamAsync"/> directly; the Worker's startup sweep stays as an idempotent
/// backfill for teams that predate this (and for any created while the Worker was down).</para>
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
                // scope of its own, and threading a clock through would buy a testable timestamp
                // nothing asserts on — the seeder's tests are about which rows appear, not when.
                UpdatedUtc = DateTime.UtcNow
            });
            logger.LogWarning("Seeded default EmailSettings for team {TeamId} ({TeamName}) with placeholder From/Reply-To/PrivacyPolicy/AdminNotification values — these must be updated before sending real candidate email",
                team.Id, team.Name);
        }

        SeedMessages(dbContext, logger, team);
    }

    /// <summary>
    /// The example messages, seeded <b>once per team, ever</b> — recorded by
    /// <see cref="Team.MessageRulesSeededUtc"/>.
    ///
    /// <para>That tombstone is load-bearing (#401 PR2). A per-message "does this team have one for
    /// this trigger?" check re-adds a message somebody deleted on the very next Worker start, quietly
    /// resuming a send they had stopped. Setting a team up is a one-time act, not an invariant to
    /// maintain, and a team that wants nothing at a trigger point is entitled to have nothing.</para>
    ///
    /// <para><b><c>CreatedUtc</c> is the risky line, and it is deliberately "now".</b> Every scan is
    /// bounded by it, so a message created at this moment never fires for a subject whose trigger
    /// moment already passed. On an existing deployment that means somebody who registered this
    /// morning and has not had their confirmation yet will not get one — accepted, and confirmed with
    /// Mike, as the price of the direction that cannot mass-mail.</para>
    /// </summary>
    private static void SeedMessages(AppDbContext dbContext, ILogger logger, Team team)
    {
        if (team.MessageRulesSeededUtc is not null)
        {
            return;
        }

        var createdUtc = DateTime.UtcNow;

        foreach (var seed in Seeds.All)
        {
            dbContext.MessageRules.Add(new MessageRule
            {
                TeamId = team.Id,
                Name = seed.Name,
                Trigger = seed.Trigger,
                ParameterHours = seed.ParameterHours,
                Subject = seed.Subject,
                Body = seed.Body,
                Channel = MessageChannel.Email,
                Recipient = seed.Recipient,
                FanOut = MessageFanOut.PerRecipient,
                IsEnabled = seed.Enabled,
                CreatedUtc = createdUtc
            });
        }

        team.MessageRulesSeededUtc = createdUtc;
        logger.LogInformation("Seeded {Count} example messages for team {TeamId} ({TeamName})", Seeds.All.Count, team.Id, team.Name);
    }

    /// <param name="Enabled">
    /// <b>Automatic messages arrive off; hand-sent ones arrive on</b> (Mike, 2026-08-21: "keep them
    /// all turned off"). The risk being avoided is unread mail going out by itself, and a message
    /// nothing sends until somebody presses a button is not that — off would leave the
    /// felony-disclosure and youth-program buttons silently doing nothing, which reads as broken
    /// rather than as safe.
    /// </param>
    /// <param name="Recipient">
    /// Ignored for a manual trigger, whose <c>LegalRecipients</c> is empty: the people are picked at
    /// send time, so the field is not even rendered on those.
    /// </param>
    private sealed record MessageSeed(
        MessageTrigger Trigger,
        string Name,
        string Subject,
        string Body,
        int? ParameterHours,
        MessageRecipient Recipient,
        bool Enabled);

    /// <summary>
    /// The seven starting messages. The wording deliberately demonstrates the formatting a team has
    /// available (headings, bold, a bullet list, links) and the tags each trigger actually supplies —
    /// a real, edit-in-place starting point rather than final copy.
    /// </summary>
    private static class Seeds
    {
        private static readonly MessageSeed RegistrationConfirmation = new(
            MessageTrigger.CandidateRegistered,
            "Registration confirmation",
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
            """,
            ParameterHours: null, MessageRecipient.Candidate, Enabled: false);

        private static readonly MessageSeed DayBeforeReminder = new(
            MessageTrigger.BeforeSessionStart,
            "Reminder 24 hours before the session",
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
            """,
            ParameterHours: 24, MessageRecipient.Candidate, Enabled: false);

        // #219: chases the FCC's own application fee, not the team's exam fee — that one is collected
        // at the session and so was never actually outstanding when the old reminder fired.
        //
        // No payment link anywhere in this body, on purpose. FCC bills the applicant directly, and
        // the team's Square link pays a different bill.
        private static readonly MessageSeed FccFeeReminder = new(
            MessageTrigger.FccFeeOutstanding,
            "FCC fee reminder after 5 days",
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
            """,
            ParameterHours: 120, MessageRecipient.Candidate, Enabled: false);

        // PaymentExpirationNotice (MessageTrigger.PaymentUnpaid) removed 2026-08-25. Mike: "PaymentUnpaid
        // is literally worthless. If they didn't pay the test session fee, they couldn't test and/or
        // the VEC would not process it." Its condition — an FCC application entered for a candidate
        // who never paid to test — cannot legitimately arise, so nothing was ever going to be sent.
        // PaymentUnpaidBeforeSession replaces the real need this was reaching for, and is not seeded
        // — like every trigger added since (CandidateTested, LicenseGranted, ...), a team opts in.

        // Sent by a per-candidate button, NOT automatically (#221). It used to fire from
        // SessionActionService.MarkCompletedAsync for anyone whose Tested flag that call flipped —
        // which meant an email about someone's felony disclosure went out as a side effect of a bulk
        // status change, and could only ever arrive AFTER the exam, when the candidate can no longer
        // easily ask anyone about it. Informational only: the club has no role beyond telling them
        // extra FCC steps are required.
        private static readonly MessageSeed FelonyDisclosureInstructions = new(
            MessageTrigger.ManualFelonyDisclosureInstructions,
            "Felony disclosure instructions",
            "Important: Additional FCC Steps Required",
            """
            <p>Hi {{CandidateName}},</p>
            <p>Because you disclosed a felony conviction on your exam application, the FCC requires an
            additional step before your license can be granted: you must submit an explanation of the
            circumstances directly to the FCC as part of your application.</p>
            <p>This is an FCC requirement, not something our club administers — we can't advise on the
            content of your submission, only let you know it's required.</p>
            <p>Questions about the process itself should go to the FCC directly.</p>
            """,
            ParameterHours: null, MessageRecipient.Candidate, Enabled: true);

        // #144. The seeded copy is more obviously a placeholder than the others, on purpose: what a
        // team points new licensees at is local knowledge this app cannot guess, and the whole point
        // of storing it is that they write it once and keep it.
        private static readonly MessageSeed GettingStartedLocally = new(
            MessageTrigger.ManualToCandidate,
            "Getting started locally",
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
            """,
            ParameterHours: null, MessageRecipient.Candidate, Enabled: true);

        // Phase 9b: a per-candidate button, only surfaced when the session's Vec.SupportsYouthProgram
        // is true.
        private static readonly MessageSeed YouthProgramInstructions = new(
            MessageTrigger.ManualYouthProgramInstructions,
            "Youth program instructions",
            "ARRL Youth Program — Discount/Reimbursement Instructions",
            """
            <p>Hi {{CandidateName}},</p>
            <p>Congratulations on your new call sign, {{CallSign}}! As a young ham, you may be eligible
            for ARRL's youth discount/FCC-fee-reimbursement scholarship program.</p>
            <p>Details and the submission form are available from ARRL — reach out to us if you have
            questions about your eligibility.</p>
            """,
            ParameterHours: null, MessageRecipient.Candidate, Enabled: true);

        public static readonly IReadOnlyList<MessageSeed> All =
        [
            RegistrationConfirmation,
            DayBeforeReminder,
            FccFeeReminder,
            FelonyDisclosureInstructions,
            GettingStartedLocally,
            YouthProgramInstructions
        ];
    }
}
