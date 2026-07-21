# VE Session Management System — Project Spec

**Purpose:** Automate the lifecycle of ham radio VE (Volunteer Examiner) test sessions — from session creation through Zoom/Discord scheduling, candidate payment, FCC license tracking, VEC submission tracking, and PII retention — with a role-based admin backend.

**Stack:** ASP.NET Core 10 (C#, LTS — released Nov 2025, supported until Nov 2028), SQLite, PowerShell 7 (for standalone jobs where useful), Discord.Net, minimal/no JS frameworks for the frontend (Razor Pages preferred, plain JS only where needed — confirm before adding any JS framework). NuGet packages are fine to use wherever they make sense (e.g. EF Core, Discord.Net, official Zoom/Square SDKs if available, `AspNet.Security.OAuth.Apple`) — no requirement to hand-roll things a well-maintained package already solves well. Prefer official/first-party SDKs where they exist; for community packages, prefer actively maintained ones (recent commits/releases) and flag the choice so it's a conscious pick, not a default.

**How to use this doc with Claude Code:** Each phase below is scoped to be implementable and testable independently. Start a new Claude Code session per phase (or per sub-task within a phase if a phase is still too large for your usage budget). Give Claude Code only the phase you're working on, plus the "Shared Data Model" section as reference. Mark phases done as you complete them.

**Testing approach:** xUnit for unit tests, matching the .NET ecosystem. Each phase includes a "Unit Tests" note calling out what to cover — write these alongside the phase's code, not as a separate pass. General rules for every phase:
- Business logic (state machines, date/threshold calculations, matching/join logic) must be unit-testable in isolation from external I/O — wrap Zoom/Discord/Square/FCC/email calls behind interfaces so they can be mocked (e.g. `IZoomClient`, `ISquareClient`, `IUlsFileParser`, `IEmailSender`)
- External API calls themselves are not unit tested against the live service — verify those manually per the phase's "Deliverable" step; unit tests instead cover your code's handling of known request/response shapes (mocked)
- File-parsing logic (ULS pipe-delimited records) gets tests against small fixture files, not live downloads
- Keep test projects mirroring the main projects (e.g. `VeSessionManager.Core.Tests` next to `VeSessionManager.Core`)

---

## Shared Data Model (reference for all phases)

This is the target end-state schema. Individual phases will create/extend subsets of this — each phase notes which tables it owns.

```
Vec
  Id
  Name
  SupportsYouthProgram (bool — e.g. ARRL's youth discount/FCC-fee-reimbursement scholarship program; flags whether the "send youth program instructions" action is available for candidates at sessions under this VEC)
  Notes (e.g. submission process quirks specific to this VEC)

EmailTemplate
  Id
  Key (identifies which automated/triggerable email this is — e.g. RegistrationConfirmation, DayBeforeReminder, PaymentReminder5Day, PaymentExpirationNotice, ArrlYouthProgramInstructions; see each phase for the specific keys it introduces and which placeholder keywords are available for that key)
  Subject
  Body (plain text/HTML with `{{PlaceholderKeyword}}` tokens, substituted at send time — e.g. `{{CandidateName}}`, `{{ZoomJoinUrl}}`, `{{PaymentLinkUrl}}`, `{{SessionDate}}`, `{{PrivacyPolicyUrl}}`; exact available keywords per template documented alongside each phase that sends that template)
  UpdatedByUserId
  UpdatedUtc

EmailSettings (added in Phase 4, not in the original model — singleton row, always Id = 1)
  Id
  FromAddress (Admin-configurable system setting per Phase 4's "From address and Reply-To address are separately configurable" note)
  FromDisplayName (nullable)
  ReplyToAddress
  PrivacyPolicyUrl (the public privacy policy link RegistrationConfirmation's {{PrivacyPolicyUrl}} placeholder uses — "from Phase 9" per the original note, but Phase 9 doesn't exist yet, so it lives here until then)
  UpdatedByUserId (nullable)
  UpdatedUtc (nullable)

FeeConfiguration
  Id
  VecId (FK -> Vec — fee schedule is tied to the VEC in effect, since switching VECs is often exactly when the fee changes)
  EffectiveDate
  FeeCollectionEnabled (bool — false if the active VEC doesn't collect a fee)
  ExamFeeAmount (nullable — total amount charged to candidate; null if FeeCollectionEnabled = false)
  RetainedAmount (nullable — portion kept for reimbursement, e.g. $7; the remainder goes to the VEC; null if FeeCollectionEnabled = false)
  Notes (e.g. "2026 ARRL fee schedule")
  CreatedByUserId
  CreatedUtc

Session
  Id
  ExamToolsSessionId (external ref)
  Title
  ScheduledStartUtc
  DurationMinutes (added in Phase 2, not in the original model — from ExamTools' sessionDef.duration; both the Zoom meeting and the Discord event need an explicit length/end time)
  ZoomMeetingId
  ZoomJoinUrl
  DiscordEventId
  ZoomDiscordSyncedStartUtc (nullable, added in Phase 2 — the ScheduledStartUtc value last successfully pushed to *both* Zoom and Discord; null means never synced. Comparing this against the current ScheduledStartUtc is Phase 2's entire "does this session need a Zoom/Discord create-or-update" signal — no separate event queue)
  VecId (FK -> Vec — denormalized copy for easy filtering/reporting without joining through FeeConfiguration)
  FeeConfigurationId (FK -> FeeConfiguration — snapshot of whichever config was active when the session was created, so historical sessions keep an accurate fee record even after rates change)
  Status (Active | Cancelled)
  CancelledUtc (nullable)
  RescheduleFlaggedForReview (bool, default false — set when a reschedule is detected while the session already has candidates; per policy, sessions should only be rescheduled with zero candidates, so this is a "something needs a human" flag, not an automatic action)
  RescheduleFlaggedUtc (nullable)
  TestingCompletedUtc (nullable — set by the Session Manager's "mark session as completed" action, see Phase 9; bulk-flips `Candidate.Tested = true` for every non-terminal candidate in the session)
  TestingCompletedByUserId (nullable)
  VecSubmissionStatus (NotSubmitted | Submitted) -- renamed from ArrlSubmissionStatus (Phase 8, 2026-07-21): submission goes to whichever VEC this session's VecId is, not always ARRL
  VecSubmittedDate (nullable)
  VecSubmittedByUserId (nullable)
  CreatedUtc

Candidate
  Id
  SessionId (FK -> Session)
  Name
  FirstName (added in Phase 4, not in the original model — sourced directly from ExamTools' separate firstname field so notification emails can open with "Hi {{CandidateFirstName}}," instead of the full Name)
  Email
  Frn (nullable — normally required before testing, but VECs have allowed testing without one during exceptional circumstances like federal shutdowns, with the candidate required to provide it afterward; Session Manager/Admin can add or edit it later once available)
  FrnMissingAtRegistration (bool — flags this case specifically, so a batch export of "no-FRN-at-time-of-test" candidates can be built later for VEC follow-up submission)
  HasFelonyDisclosure (bool, nullable — captured from the candidate's exam application data if the ExamTools/HamStudy API exposes it; **open item:** confirm during Phase 1 whether this field is actually available from the library before relying on it — don't assume the shape until you're looking at real API responses. Treated as sensitive PII, included in Phase 10's purge alongside Name/Email/Frn)
  DateRegisteredUtc
  ApplicationStatus (Unmatched | Received | Granted | Failed | NotTested)
    -- Failed = took the exam, did not pass (Session Manager marks manually; PII retained until Phase 10's scheduled purge window, same as Granted, since this is still relevant to VEC/ARRL attendance reporting)
    -- NotTested = withdrew or no-showed (Session Manager marks manually via the "delete" action — see Phase 9; PII is nulled immediately at that moment, not on a delayed schedule, since a no-show has no reporting relevance to preserve)
    -- Granted, Failed, and NotTested are all terminal — once set, the FCC watcher (Phase 5) stops matching this row
  Tested (bool, default false — flips to true when the Session Manager marks the whole session as completed, see Session.TestingCompletedUtc below. This is intentionally separate from ApplicationStatus: `Unmatched` just means "the FCC hasn't shown a match yet," which lags a day or more behind reality regardless of whether the candidate actually tested, so it can't safely be used to gate Move/Delete on its own)
  ApplicationDateEnteredUtc (nullable, from ULS HD status date — only applies to the Received/Granted path)
  CallSign (nullable, set on Granted)
  LicenseGrantDateUtc (nullable, set on Granted)
  ResultMarkedByUserId (nullable — Session Manager who marked Failed/NotTested, for audit)
  ResultMarkedUtc (nullable)
  PiiPurgedUtc (nullable — set when PII nulled)
  RegistrationConfirmationSentUtc (nullable, added in Phase 4 — send-once tracking, same idiom as Session.ZoomDiscordSyncedStartUtc)
  DayBeforeReminderSentUtc (nullable, added in Phase 4 — prevents a same-day job restart from re-sending)

Payment
  Id
  CandidateId (FK -> Candidate)
  Reason (InitialExam | Retest) -- a candidate can retest within the same session without re-registering, but owes a second fee — this is why payments are their own table instead of flat fields on Candidate
  Amount (snapshot from the session's FeeConfiguration at time of creation)
  Status (Unpaid | Paid | NotApplicable)
  PaymentLinkUrl (nullable — null if Status = NotApplicable)
  SquarePaymentReferenceId (nullable)
  PaidDateUtc (nullable)
  ExpiredUnpaid (bool, true if the 10-day unpaid window passed — see Phase 6)
  PaymentReminderSentUtc (nullable)
  RefundRequested (bool, default false — actual refund is processed manually in the Square dashboard, this is just a note for tracking)
  RefundRequestedByUserId (nullable)
  RefundRequestedUtc (nullable)
  RefundNotes (nullable)
  CreatedUtc

VolunteerExaminer
  Id
  Name
  CallSign
  Frn (nullable)

SessionVolunteerExaminer (join table)
  SessionId (FK)
  VolunteerExaminerId (FK)

User (admin backend)
  Id
  Name
  Email
  Role (SystemAdmin | TeamAdmin | SessionManager | TeamLead) -- expanded from (Admin | SessionManager | TeamLead) in Phase 9a (2026-07-21); see docs/admin-auth.md
  TeamId (nullable — null for SystemAdmin/deployment-wide; the TeamAdmin/SessionManager's own team otherwise. Added in Phase 9a, not in the original shared model.)
  ManagedByUserId (nullable — TeamLead's assigned manager, a SessionManager or TeamAdmin)

AuditLog
  Id
  UserId
  Action
  EntityType
  EntityId
  TimestampUtc
  Details

JobRunHistory
  Id
  JobName
  StartedUtc
  CompletedUtc
  Success (bool)
  ErrorMessage (nullable)
```

---

## Phase 0 — Project Foundation

**Goal:** Scaffolding only. No business logic yet.

- New ASP.NET Core 10 solution: Worker Service project (background jobs) + separate ASP.NET Core web project (admin backend), sharing a class library for data models/EF Core context
- SQLite via EF Core, connection string in `appsettings.json`
- Create the full Shared Data Model above as EF Core entities + initial migration
- Basic `JobRunHistory` logging helper (any background job wraps its run in a try/catch that logs start/end/success/error to this table) — build this now since every later phase's jobs will use it
- Deployment target: Linux via systemd, matching your existing NcsScheduler pattern (note this to Claude Code as a reference pattern, not a file to copy)
- Source control: private GitHub repository, deployed similarly to NcsScheduler — set this up as part of this phase (repo created as private, not public)
- Deployment trigger: **only on tags, not on every commit to main/master** — set up the deploy workflow (e.g. GitHub Actions) to fire on tag push (e.g. `v1.0.0`) rather than continuous deployment on every push, so releases are deliberate

**Unit Tests:** Test project scaffolded now (`VeSessionManager.Core.Tests`), even though there's little logic yet — establishes the pattern for every later phase. Cover the `JobRunHistory` logging helper (success path, exception-caught path).

**Deliverable:** Solution builds, migration creates the DB, a dummy "hello world" job runs on a timer and logs to `JobRunHistory`.

---

## Phase 1 — ExamTools/HamStudy Session Ingestion

**Goal:** Detect new sessions and candidates from your HamStudy library, populate `Session` and `Candidate`.

- Integrate your existing HamStudy client library (you'll provide the repo/details to Claude Code directly in this phase's session)
- Polling job: checks for new sessions since last run, inserts `Session` rows
- Polling job: checks for new/changed candidate registrations per session, inserts/updates `Candidate` rows (Name, Email, FRN, DateRegisteredUtc)
- **Reschedule/cancellation detection:** for already-known sessions, compare the polled data against the stored `Session` record each run:
  - If `ScheduledStartUtc` differs from the stored value: this is a reschedule. If the session currently has zero non-terminal candidates, apply the new time and let Phase 2 update Zoom/Discord automatically. If it has one or more candidates, **do not auto-apply the change** — set `RescheduleFlaggedForReview = true`, `RescheduleFlaggedUtc = now`, log to `AuditLog`, and leave the old `ScheduledStartUtc` in place until a Session Manager reviews it (per policy, sessions should only be rescheduled with zero candidates, so this case means something needs manual attention, not automation)
  - If the session is removed/cancelled in the source data: set `Status = Cancelled`, `CancelledUtc = now`, hand off to Phase 2 for Zoom/Discord cleanup. **Confirmed against real API responses:** ExamTools/HamStudy has no explicit "cancelled" state/flag — a cancellation is either a reschedule (handled by the rule above) or the session simply no longer appearing in the polled feed by its known session ID. Detection is therefore: any previously-ingested, non-terminal `Session` whose `ExamToolsSessionId` is absent from the latest poll is treated as cancelled.
- No external side effects yet (no Zoom/Discord/Square/email) — this phase is data ingestion only, so it's testable in isolation

**Unit Tests:** Mock the HamStudy client interface; test that new-session and new-candidate detection correctly diffs against existing DB rows (no duplicates on re-poll, correctly picks up genuinely new records, correctly updates changed fields on existing records).

**Deliverable:** Running the poll job against real data populates `Session`/`Candidate` correctly, verified by querying the DB directly.

---

## Phase 2 — Zoom + Discord Event Creation

**Goal:** On new session detected, create Zoom meeting and Discord scheduled event.

- Zoom Server-to-Server OAuth app, `POST /users/{userId}/meetings` — reference: https://developers.zoom.us/docs/api/meetings/
- Store `ZoomMeetingId` / `ZoomJoinUrl` on `Session`
- Discord.Net bot, `CreateEventAsync` on the guild — reference: https://discordnet.dev/guides/events/events.html
- Event description/location includes the Zoom join link
- Store `DiscordEventId` on `Session`
- **Do not use Discord's native recurrence** — each session is a one-off event with its own explicit date/time computed from the session data, not a recurring rule (avoids the documented Discord recurrence bugs discussed earlier)
- Trigger: hook this into the end of Phase 1's "new session detected" path
- **Reschedule (zero-candidate case only, per Phase 1):** update the *existing* Zoom meeting (Zoom's update meeting endpoint) and the *existing* Discord event (Discord.Net's event modify call) to the new date/time — do not delete and recreate, so the same `ZoomMeetingId`/`DiscordEventId`/join link stay valid
- **Cancellation:** cancel/delete the Zoom meeting and delete the Discord event. This happens automatically regardless of candidate count — it's infrastructure cleanup, not candidate communication. **Do not send any candidate-facing notification from this job** — per policy, communicating a cancellation to registered candidates is handled manually by the Session Manager, not automated
- **Implemented as a scan, not an event queue:** rather than reacting to Phase 1's "new session detected" as a discrete event, `SessionEventSchedulingService` scans `Session` each run for `Status = Active && ScheduledStartUtc != ZoomDiscordSyncedStartUtc` (needs create-or-update: null `ZoomMeetingId`/`DiscordEventId` means create, non-null means update) and `Status = Cancelled && (ZoomMeetingId or DiscordEventId still set)` (needs cleanup). This makes the "Zoom succeeds, Discord fails" case from the Unit Tests note self-healing: the failed run leaves state that the *next* run picks up automatically (Zoom is skipped since its id is already set; Discord is retried since its id is still null) — no separate retry/flag bookkeeping needed. Still triggered from the same Worker tick as Phase 1's ingestion, immediately after it, per "hook this into the end of Phase 1's new session detected path," but as its own `JobRunHistory`-tracked step (`SessionEventScheduling`) so a scheduling failure doesn't read as an ingestion failure or vice versa.
- **Zoom client is hand-rolled** (plain `HttpClient`, no NuGet package) — Zoom does not publish an official lightweight .NET SDK for this Server-to-Server OAuth + Meetings API surface, so this follows the same pattern as `ExamToolsClient`. **Discord.Net.Rest** (the REST-only, no-gateway-connection flavor — this job never needs to listen for Discord events) is used for Discord, per the Stack section's pre-approval of Discord.Net.
- Zoom Server-to-Server OAuth token endpoint (`POST https://zoom.us/oauth/token`, `grant_type=account_credentials`) confirmed live; access tokens last 1 hour with no refresh token, so `ZoomClient` caches and re-requests one a minute before expiry.
- **Retrofitted in Phase 4 to make both Zoom and Discord optional** (user request, not in this phase's original description — only ExamTools remains a hard requirement, since ingestion is what everything else depends on). `IZoomClient.IsConfigured`/`IDiscordEventClient.IsConfigured` gate each independently; `ZoomDiscordSyncedStartUtc` only advances once both `ZoomMeetingId` and `DiscordEventId` are actually set (never "or unconfigured" — a session with one or both unconfigured just stays pending, logged once in aggregate per poll, and backfills automatically the moment credentials are added). Discord structurally needs the Zoom join link for its description/location, so it stays pending even if Discord itself is configured until Zoom has actually produced one. See `docs/zoom-discord-scheduling.md` and the CLAUDE.md gotcha about writing this kind of "settled" check correctly.

**Unit Tests:** Mock `IZoomClient`/Discord.Net client interfaces; test that the correct date/time and Zoom link get passed into the Discord event creation call, and that `ZoomMeetingId`/`ZoomJoinUrl`/`DiscordEventId` are persisted correctly on `Session`. Test failure handling (e.g. Zoom succeeds but Discord call fails — confirm behavior, likely retry or flag rather than silently losing the Zoom meeting). Test that reschedule calls the update endpoints (not create) and preserves the existing IDs. Test that cancellation triggers cleanup calls but never triggers an email send.

**Deliverable:** Creating a test session in ExamTools results in a real Zoom meeting and a Discord event appearing with the correct date and Zoom link, within one poll cycle.

---

## Phase 3 — Square Payment Links + Webhook (Initial Fee + Retests)

**Goal:** Generate payment links per `Payment` record (not per candidate directly — a candidate may owe more than one fee if they retest within the same session), track payment status via webhook, only when the active VEC actually collects a fee.

- On new candidate registration: create one `Payment` row, `Reason = InitialExam`, `Amount` = snapshot of `Session.FeeConfiguration.ExamFeeAmount`
- If `Session.FeeConfiguration.FeeCollectionEnabled = false`: set that `Payment.Status = NotApplicable` immediately, skip link generation and all downstream reminder logic for it
- If fee collection enabled: Square Payment Links API, `POST /v2/online-checkout/payment-links` — reference: https://developer.squareup.com/docs/checkout-api/quickstart
- Include the `Payment` row's ID (not the candidate's) as the reference ID on the link/order, so retest payments and initial payments are distinguishable in the webhook
- Store `PaymentLinkUrl` / `SquarePaymentReferenceId` on the `Payment` row
- **Retest flow:** if a candidate fails and retests within the same session (no re-registration), the Session Manager triggers a "create retest payment" action (surfaced in Phase 9's admin UI) which creates a second `Payment` row, `Reason = Retest`, same amount logic as above. A candidate can end up with 0 (no-fee VEC), 1, or multiple `Payment` rows.
- Webhook endpoint (ASP.NET Core controller/minimal API) subscribed to `payment.updated` — reference: https://developer.squareup.com/docs/payments-api/webhooks
- **Verify Square's webhook signature** on every incoming request before processing — do not trust an unverified POST as a real payment confirmation
- On `status = COMPLETED` webhook: match reference ID to `Payment`, set `Status = Paid`, `PaidDateUtc`
- Webhook endpoint must respond 2xx quickly (per Square's retry behavior) — do heavy processing async if needed
- **Confirmed against real API responses:** `payment.updated`'s payload does not include the order's `reference_id` — only `order_id`. Matching is therefore by Square's own `order_id` (stored as `Payment.SquarePaymentReferenceId`), not by literally reading `reference_id` back out of the webhook; each `Payment` still gets its own distinct `Order`/`order_id` when its link is created, so initial and retest payments remain independently distinguishable, just via a different field than a literal reading of "distinguishable in the webhook" implied. See `docs/square-payments.md`.
- **Implemented as a scan, not an event queue** (same reasoning as Phase 2): `PaymentGenerationService` runs two passes each poll — candidates with no `InitialExam` `Payment` row yet get one created; `Unpaid` payments with no `PaymentLinkUrl` yet get a Square call. A Square failure leaves `PaymentLinkUrl` null, so the *next* poll retries just the link, not the whole row — self-healing without a queue.
- **Lesson from a real deploy:** the credential-check for each external API client (`ExamToolsClient`, `ZoomClient`, `DiscordEventClient`, `SquareClient`) must happen lazily, at first *use*, never in the constructor — these singletons are resolved from inside a Worker `BackgroundService`, and a constructor throw there is host-stopping (.NET's default `BackgroundServiceExceptionBehavior` is `StopHost`), taking down every other poller, not just the one missing credentials.
- **Square is an optional integration, not a hard dependency** (clarified after the fact — not explicit in the original phase description, but confirmed as intended): an org that doesn't collect fees online yet, or hasn't finished Square account setup, must not see repeated failed-call errors every poll. `ISquareClient.IsConfigured` (true once `Square:AccessToken` is set) gates whether `PaymentGenerationService` even attempts link generation. `Payment` rows are still created normally either way (so downstream phases have consistent data to work with); when Square isn't configured, unpaid links are simply left ungenerated and reported once per poll at `INFO`, not `ERROR` — and back-generate automatically, with zero other config change, the moment credentials are added.

**Unit Tests:** Mock `ISquareClient` for link creation (correct reference ID, correct amount from the session's fee config, correct `Reason`). Test the `FeeCollectionEnabled = false` path skips link generation and marks `NotApplicable`. Test webhook signature verification rejects unsigned/invalid requests. Feed in sample `payment.updated` JSON payloads (COMPLETED and non-COMPLETED statuses) and verify correct `Payment` matching and status updates; test the case of an unrecognized/unmatched reference ID (should not throw, should log). Test that a candidate with two `Payment` rows (initial + retest) tracks each independently.

**Deliverable:** A test payment link generated for a test candidate, paid in Square sandbox, correctly flips that `Payment.Status` within seconds via webhook. A second test with `FeeCollectionEnabled = false` confirms no link is generated. A third test confirms a retest payment can be created and paid independently of the initial payment.

---

## Phase 4 — Candidate Notification Emails + Configurable Email Templates

**Goal:** Registration confirmation + day-before reminder emails — built on a configurable template system from the start, since every email in the system should be editable without a code change.

**Template engine (build this first, then wire the two emails below into it):**
- `EmailTemplate` table (already in shared model) holds Subject/Body per `Key`, with `{{Placeholder}}` tokens
- A simple substitution service: given a `Key` and a dictionary of placeholder values, loads the template, replaces tokens, returns the final subject/body. Unknown placeholders left in the body (typo, missing value) should be logged as a warning, not silently sent to a candidate with a literal `{{Typo}}` in it
- Seed the two templates this phase introduces on first run (`RegistrationConfirmation`, `DayBeforeReminder`) with reasonable default content, but treat that content as a starting point the Admin can edit, not the source of truth going forward
- Every later phase that sends an email (Phase 6's reminders, Phase 9's ARRL youth program action, anything future) should use this same engine and register its own `EmailTemplate.Key` rather than hardcoding content — note this explicitly to Claude Code so the pattern is followed consistently

**RegistrationConfirmation** — sent on new candidate detected (end of Phase 1's candidate ingestion)
- Available placeholders: `{{CandidateName}}`, `{{SessionDate}}`, `{{ZoomJoinUrl}}`, `{{PaymentLinkUrl}}` (empty string if `FeeCollectionEnabled = false` — template content should be written to read sensibly either way), `{{PrivacyPolicyUrl}}` (links to the public privacy policy page from Phase 9)

**DayBeforeReminder** — daily job, finds candidates whose session is tomorrow **and `Session.Status = Active`**
- Available placeholders: `{{CandidateName}}`, `{{SessionDate}}`, `{{ZoomJoinUrl}}`, `{{OutstandingPaymentLinkUrl}}` (empty string if nothing outstanding) — sent regardless of payment status, this reminder is about the session itself

- Use SMTP directly in C# unless you already have a transactional email service in mind — confirm with Claude Code before adding a third-party email SDK
- **From address and Reply-To address are separately configurable** (Admin-configurable system setting, not hardcoded) — every email sent through the template engine uses the configured "From" for the sender but sets the SMTP `Reply-To` header to a separately configured address, so replies land wherever you want them even though the send-from address is different
- **Confirmed with the user: MailKit** (not the built-in, Microsoft-discouraged `System.Net.Mail.SmtpClient`), sending via **Mailgun's** SMTP endpoint (`smtp.mailgun.org:587`, STARTTLS) — the user already runs SMTP infrastructure and picked Mailgun as the actual provider. From/Reply-To/PrivacyPolicyUrl live in a new singleton `EmailSettings` row (added to the Shared Data Model; not explicitly named in the original Phase 4 text, which only described the *requirement* that they be admin-configurable, not where).
- **`{{CandidateFirstName}}` added** alongside `{{CandidateName}}` on both templates (not in the original placeholder list) — sourced from ExamTools' own `firstname` field via a new `Candidate.FirstName` column, so "Hi {{CandidateFirstName}}," reads naturally instead of the full registered name.
- **SMTP is optional**, same retrofit as Phase 2's Zoom/Discord (see that phase's note) — `IEmailSender.IsConfigured` (host *and* username both set — a bare default hostname isn't enough, see the CLAUDE.md gotcha) gates sending; `RegistrationConfirmationSentUtc`/`DayBeforeReminderSentUtc` stay null and backfill automatically once SMTP is configured, exactly like Payment's Square gate.
- **Editing seeded content today:** since Phase 9's admin UI doesn't exist yet, `docs/email-notifications.md` documents editing `EmailTemplates`/`EmailSettings` directly via a SQLite browser — the seeder never overwrites a row that already exists for a given `Key`, so this is safe to do immediately after first run and stays safe on every future deploy.

**Unit Tests:** Test the template substitution engine directly (known placeholders replaced correctly, unknown placeholders logged not silently sent, missing/empty values handled per-template as documented above). Mock `IEmailSender`; test that the "day-before" query correctly selects candidates whose session is exactly tomorrow (boundary cases: today, day-after-tomorrow should be excluded) and that the correct placeholder values are passed into the template engine per candidate.

**Deliverable:** Test candidate receives correct emails at both trigger points, content editable via a direct `EmailTemplate` row edit (UI for editing comes in Phase 9, but the mechanism must work without it for this phase's deliverable).

---

## Phase 5 — FCC ULS Application/License Watcher

**Goal:** Track candidate FRNs against FCC's daily ULS data to detect application receipt and license grant.

**Reference facts (verified, give these to Claude Code directly):**
- Daily amateur application files: `a_am_<day>.zip` (e.g. `a_am_wed.zip`)
- Daily amateur license files: `l_am_<day>.zip`
- Host: `https://data.fcc.gov/download/pub/uls/daily/`
- Weekly full files (fallback for missed days): `a_amat.zip`, `l_amat.zip`, same host under `/complete/`
- Files are pipe-delimited text inside the ZIP, multiple record types per file
- Relevant record types: `EN` (entity — contains FRN), `HD` (application/license header — contains status code + status date), `AM` (amateur-specific — contains Call Sign)
- Join key: Unique System Identifier (numeric(9,0)), present on all three record types
- Field layout reference: https://www.fcc.gov/file/13762/download
- Full field/SQL definitions: https://www.fcc.gov/file/16383/download
- Files are generated ~5am ET Tue–Sat, each containing the prior day's transactions; note that maintenance windows can suspend a day's processing (e.g. weekend maintenance) — the weekly fallback exists for this reason

**State machine (implement exactly as specified):**
- `Candidate.ApplicationStatus` starts `Unmatched`
- **Terminal statuses (`Granted`, `Failed`, `NotTested`) are permanently excluded from this job.** Once a candidate is in a terminal state, neither the daily nor weekly file processing touches that row again, regardless of what appears in later ULS files.
- **Candidates with a null `Frn`** are skipped entirely by this job (nothing to match on) until a Session Manager/Admin adds one — at which point normal matching resumes on the next run.
- Daily job downloads and parses that day's `a_am_<day>.zip`; for each `Unmatched` candidate, check if FRN appears → set `Received`, set `ApplicationDateEnteredUtc` from the `HD` status date
- Daily job also downloads and parses that day's `l_am_<day>.zip`; for any **non-terminal** candidate (`Unmatched` or `Received`) check if FRN appears with a new license → set `ApplicationStatus = Granted`, set `CallSign`, set `LicenseGrantDateUtc`. **License match always wins and short-circuits application status** — a license can be found even if the application file was never matched.
- Weekly full-file job runs on a schedule (e.g. Monday) as a catch-up pass over all non-terminal candidates, in case a daily file was missed

**Open item — upgrade exams (existing licensees):** a candidate upgrading their class (e.g. Technician → General) already has a license on file *before* the session, so "FRN appears in the license file" isn't sufficient on its own to detect the *new* grant — the ULS record needs to show a change (new operator class / new grant date on the existing call sign) rather than a first-time appearance. This needs real sample data from both the FCC ULS files and the ExamTools/HamStudy API (to see what prior-license info, if any, is available at registration) before the exact matching logic can be designed. Treat this as a follow-up design pass once that data is in hand — do not guess at the logic in this phase.

**Unit Tests:** Build small fixture pipe-delimited files (a handful of `EN`/`HD`/`AM` rows including known-match and known-non-match FRNs) and test the parser/join logic against them directly — no live download needed. Test the state machine explicitly: `Unmatched → Received`, `Received → Granted`, and the critical `Unmatched → Granted` short-circuit path (license found with no prior application match). Test that already-`Granted` candidates are skipped entirely.

**Deliverable:** Given a test FRN known to be in a recent ULS daily file, the job correctly transitions candidate status. Given a test FRN with a known license grant, status jumps straight to `Granted` even if `Received` was never set.

---

## Phase 6 — Payment Reminder & Expiration Job

**Goal:** Nudge unpaid candidates, flag stale unpaid applications, notify you at expiration.

- Daily job, operates per unpaid `Payment` row (not per candidate — a candidate with a retest may have two independent payments in flight); skips any `Payment` where `Status = NotApplicable`, skips any `Payment` whose `Candidate.ApplicationStatus` is terminal (`Granted`, `Failed`, `NotTested`), and skips any `Payment` whose `Session.Status = Cancelled`
- **5-day reminder:** `Payment.Status = Unpaid`, associated `Candidate.ApplicationStatus = Received`, and `Candidate.ApplicationDateEnteredUtc <= Today - 5 days` → send `PaymentReminder5Day` email template (placeholders: `{{CandidateName}}`, `{{ZoomJoinUrl}}`, `{{PaymentLinkUrl}}`) to candidate, only once (`Payment.PaymentReminderSentUtc`)
- **10-day expiration:** `Payment.Status = Unpaid` and `Candidate.ApplicationDateEnteredUtc <= Today - 10 days` → set `Payment.ExpiredUnpaid = true`, send `PaymentExpirationNotice` email template (placeholders: `{{CandidateName}}`, `{{SessionDate}}`, `{{PaymentAmount}}`) **to Mike** (not the candidate), stop further reminders for that payment
- Candidates with `ApplicationStatus = Unmatched` are excluded from both triggers (no application date to count from) — instead, flag separately for manual review if `Unmatched` persists beyond some reasonable window (use a config value, default 5 days from `DateRegisteredUtc`, distinct from the payment logic)

**Note — reminder gating vs. retests:** as written, the 5/10-day reminder logic is gated on `ApplicationStatus = Received`, which only happens once a candidate passes and their FCC application shows up. A candidate who fails and immediately retests within the same session may owe a fee before there's any FCC application to gate on. If that turns out to be a real gap once Phase 1/9 workflows are in daily use, the fix is likely: gate retest payment reminders on the Session Manager having marked *some* result (anything other than still-pending) rather than on FCC application status. Flagging this now rather than solving it blind — revisit once you're running real sessions through it.

**Unit Tests:** Table-driven tests covering every status/date combination described above — exactly-5-days, before-5-days, after-5-days for the reminder; exactly-10-days, before, after for expiration; `Granted` short-circuit; `Unmatched` manual-review flag boundary. This phase is pure date/status logic with no external I/O, so it should have the most thorough test coverage of any phase.

**Deliverable:** Simulated test candidates at each status/date combination produce exactly the expected email/flag outcome.

---

## Phase 6.5 — Multi-Team Foundation

**Goal:** Let one deployment serve more than one independent VE team (each with their own
ExamTools/Zoom/Discord/Square/Email credentials), inserted ahead of Phase 9's admin backend so
authorization scoping is built team-aware from the start rather than retrofitted later.

**Hierarchy: VEC ⇒ Team ⇒ VE, not the reverse.** A `Vec` is the FCC-recognized coordinating org
(ARRL, W5YI, etc.) that dictates fees — it stays a shared/global reference table (one real-world
"ARRL" row, not one per team), since a VEC dictates fees universally, not per-team-negotiated. The
new `Team` entity is the group of VEs operating a deployment, holding integration credentials;
individual VEs (`VolunteerExaminer`, Phase 7) belong to a Team. `Session` gained a `TeamId` FK
independent of its existing `VecId` — no relationship between `Vec` and `Team` themselves, so a
team can work with multiple VECs and a VEC can be shared by multiple teams.

**Scope: ExamTools only, deliberately narrow.** `ExamToolsClient` reworked from one process-lifetime
singleton credential set to one internal per-`TeamId` cache (own `HttpClient`/cookie-jar/login-state
per team, still a single `AddSingleton` — no keyed DI). `SessionIngestionService.RunAsync` takes a
`Team`; `SessionIngestionJob` loops every `Team` for the ingestion step, then still runs the
Zoom/Discord scheduling, Square payment-link, and registration-confirmation steps globally (one
call per tick, shared account) exactly as before. Credentials moved off appsettings/user-secrets
onto `Team` columns, following the plaintext-in-SQLite approach `EmailSettings` already used.

**Deferred fast-follow, not built in this pass:** the same per-`TeamId` client pattern applied to
Zoom/Discord/Square/Email, plus a per-team Square webhook route (signature verification needs the
right team's key *before* the payload can be parsed to find which team it belongs to — likely
`/webhooks/square/{teamSlug}`). See `docs/multi-team.md` for the full design and the template to
repeat.

**Unit Tests:** `SessionIngestionServiceTests` covers a shared-`Vec`-across-two-teams case (proves
sessions from different teams correctly attribute to one shared VEC/fee schedule while getting
distinct `TeamId`s) and a cross-team cancellation-false-positive case (one team's poll must never
see another team's still-active session as "disappeared").

**Deliverable:** Two `Team` rows, each with their own ExamTools credentials, both correctly ingest
their own sessions on the same deployment with no cross-team interference.

---

## Phase 7 — VE Tracking

**Goal:** Track which VEs worked which sessions.

- `VolunteerExaminer` and `SessionVolunteerExaminer` tables (already in shared model)
- Data entry: manual (via admin backend, Phase 9) or ingested from ExamTools session data if your library exposes VE roster per session — check during this phase and use whichever is available
- Simple report: session count per VE, filterable by date range

**Unit Tests:** Test the session-count aggregation query logic (correct counts, correct date-range filtering) against a seeded in-memory/test DB.

**Deliverable:** VE roster correctly attached to sessions, count report accurate.

---

## Phase 8 — VEC Submission Tracker

**Renamed from "ARRL Submission Tracker" (2026-07-21, user request):** submission goes to
whichever VEC a session is actually under (`Session.VecId`), not always ARRL, so the phase and its
fields are named generically. `Vec.SupportsYouthProgram` and the `ArrlYouthProgramInstructions`
email template (Phase 9) are **not** part of this rename — those are genuinely ARRL-specific
features (ARRL's own youth discount program), not a generic VEC concept.

**Goal:** Track submission status per session (manual process, not automated).

- `Session.VecSubmissionStatus`, `VecSubmittedDate`, `VecSubmittedByUserId` (already in shared model as `Arrl*`; renamed Phase 8)
- Session detail view (Phase 9 admin backend): toggle Not Submitted → Submitted, captures date + user automatically
- Dashboard indicator: count of sessions pending VEC submission (e.g., sessions with `Granted` or otherwise-complete candidates where status is still `NotSubmitted`)

**Unit Tests:** Test the toggle logic (status transition, date/user capture) and the "pending submission" dashboard query.

**Deliverable:** Toggling submission status on a test session persists correctly with audit info.

---

## Phase 9 — Admin Backend (RBAC)

**Design checkpoint:** This is the only phase with a real user-facing UI (Phases 0–8 are background jobs and API integrations with no interface). Before starting the build, do a design pass with Claude Design to mock up the role views — settle on layout/look first, then hand the approved mockups to Claude Code as part of this phase's brief so it builds to a spec rather than improvising UI as it goes.

**This phase is large enough to split into four sub-phases.** Run each as its own Claude Code session, in order — 9a establishes the auth/scaffolding everything else builds on.

**Roles — expanded to four during Phase 9a (2026-07-21, user request), not the three originally described here.** The original single "Admin" role didn't fit well once the multi-team foundation (Phase 6.5) gave each `Team` its own credentials/settings — see `docs/admin-auth.md` for the full rationale. `User.Role` (`UserRole` enum) is now `{ SystemAdmin, TeamAdmin, SessionManager, TeamLead }`:
- **SystemAdmin** (renamed from "Admin"): full system access — user management (create/deactivate, assign roles, assign Team Leads to Session Managers/Team Admins), creates `Team` rows and grants the TeamAdmin role, system config that's genuinely deployment-wide (ULS polling settings, PII retention window setting, VEC management — add/edit `Vec` records including `SupportsYouthProgram`, since VECs are shared/global not per-team), global visibility across all sessions, full audit log, job run history/ops dashboard (see `JobRunHistory` table). Kept as a full superset of every role below it, not narrowed to provisioning-only.
- **TeamAdmin** (new): controls all settings within their own team — that team's Zoom/Square/Discord/Email credentials (now per-`Team`, see the multi-team foundation), fee configuration (`FeeConfiguration` CRUD for VECs their team works with), email template management for their team's `EmailTemplate` rows — plus everything Session Manager can do (a superset) within their own team, plus granting SessionManager/TeamLead to users within their team.
- **Session Manager:** full visibility/edit on their own team's sessions — candidate table (registration, payment, application, license status), manual actions:
  - resend reminder email, mark paid manually for edge cases, add walk-in candidate, add/edit a candidate's FRN if missing at registration
  - "Mark session as completed" — one action, set once the testing is done for the day. Sets `Session.TestingCompletedUtc`/`TestingCompletedByUserId`, and bulk-sets `Candidate.Tested = true` for every candidate in the session still in a non-terminal `ApplicationStatus` (`Unmatched` or `Received`) — candidates already marked `Failed`/`NotTested` before this point are left alone. For each candidate who passed this way (i.e. `Tested` just flipped to `true`, not previously marked `Failed`) and has `HasFelonyDisclosure = true`, automatically send the `FelonyDisclosureInstructions` email template (placeholders: `{{CandidateName}}`) — this only tells them special FCC steps are required, the club has no role beyond informing them
  - mark a candidate Failed (took the exam, didn't pass — retained per Phase 10's normal delayed purge window)
  - "Delete" a candidate (withdrew or no-showed — only available while `Candidate.Tested = false`, i.e. before the session is marked completed; this action sets `ApplicationStatus = NotTested` and immediately nulls PII fields — `Name`, `Email`, `Frn`, `HasFelonyDisclosure` — keeping a stub row for stats — distinct from Phase 10's scheduled purge, it happens right at the moment of the action)
  - "Move" a candidate to a different session — only available while `Candidate.Tested = false`, and only between sessions under the same `Vec` (moving across VECs isn't supported; if it's ever needed, per Mike this is rare enough — maybe once or twice a decade — that it can be handled manually rather than built). The candidate's existing `Payment` row(s) carry over unchanged; no new charge is generated
  - "Send ARRL Youth Program instructions" — button on a candidate row, only visible when the session's `Vec.SupportsYouthProgram = true`. Sends the `ArrlYouthProgramInstructions` email template (placeholders: `{{CandidateName}}`, `{{CallSign}}` if set) with instructions for the discount/FCC-fee-reimbursement scholarship form. This is a manual, per-candidate trigger (not automatic) since youth-program eligibility isn't something the system can determine on its own
  - create a retest payment for a candidate who fails and retests within the same session
  - flag a payment as "refund requested" with notes — the actual refund is processed manually in the Square dashboard, this is tracking-only
  - review and clear a session's `RescheduleFlaggedForReview` flag once they've manually communicated the change to candidates and confirmed the new date
  - VE roster editing, VEC submission toggle
- **Team Lead:** scoped to sessions they're assigned to — **confirmed in Phase 9a**: via the existing `User.ManagedByUserId` (single assignment, role-agnostic — the assigned manager can be a Session Manager or a Team Admin, effective team is whoever that manager's `TeamId` is), no multi-manager join table for now, revisit only if a real need for multi-manager visibility comes up. Same status view as Session Manager including full PII (Name, Email, FRN) — needed for day-of check-in and identity verification against photo ID, not masked, but read-only except where explicitly decided otherwise (confirm with Mike before granting Team Leads any write access beyond viewing)

---

### Phase 9a — Auth + Scaffolding

**Done, 2026-07-21 — see `docs/admin-auth.md` for full detail.**

**Goal:** Get a logged-in session working for every role, with no real feature content yet — just a shell to build the rest on top of.

- ASP.NET Core Identity, supporting multiple sign-in methods:
  - Username/password (native Identity)
  - Google (`Microsoft.AspNetCore.Authentication.Google` — standard `AddGoogle(...)`)
  - Microsoft (`Microsoft.AspNetCore.Authentication.MicrosoftAccount` — standard `AddMicrosoftAccount(...)`, pairs naturally with existing Entra ID familiarity)
  - Apple (community package `AspNet.Security.OAuth.Apple` — more involved setup: requires a signed JWT client secret generated from a `.p8` private key, Team ID, and Key ID from an active Apple Developer account; confirm the $99/year Developer account cost is worth it before committing to this provider, versus just Google + Microsoft + username/password)
  - Reference: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/?view=aspnetcore-10.0 and https://github.com/aspnet-contrib/AspNet.Security.OAuth.Providers/blob/dev/docs/sign-in-with-apple.md
- Role assignment (`User.Role`) and the authorization scoping rules described above — build the *mechanism* (policies/handlers that filter queries by role) even though there's barely any real data to filter yet. Landed as `SessionAccessScope` (`VeSessionManager.Core/Authorization/`), plain C# so it's unit-tested without a web host.
- Basic navigation shell reflecting every role (empty/placeholder pages are fine — this phase proves the auth and scoping work, not the features)
- **Built username/password + Google + Microsoft; Apple deferred** (confirmed with the user — matches this section's own suggested fallback)

**Unit Tests:** Authorization/scoping logic is the priority here — test that role-based queries correctly filter data (SessionManager/TeamAdmin see only their own team's sessions, TeamLead sees only their assigned manager's team's, SystemAdmin sees all), and that write-permission checks correctly reject unauthorized actions. External OAuth provider flows themselves are not unit tested (verify those manually per provider in a real browser) — landed as `SessionAccessScopeTests.cs`; account-linking logic is deliberately a single trusted `UserManager.FindByEmailAsync` call, not bespoke logic, so it didn't need its own tests beyond that judgment call being documented in `docs/admin-auth.md`.

**Deliverable:** Four test users, one per role, can log in via username/password, and see correctly scoped (even if mostly empty) data. Live-verified via a real browser click-through: login → correct role landing page → denied access to another role's page → logout → login as a different role → correct landing page again.

---

### Phase 9b — Session Manager Candidate Actions

**Done, 2026-07-21 — see CLAUDE.md's "Session Manager candidate actions" entry for full detail.**

**Goal:** The full candidate table and all the manual actions listed under Session Manager above.

- Session list + session detail view with the candidate table (registration, payment, application, license status)
- Every action listed under "Session Manager" above, wired to the real underlying logic built in Phases 1–8
- VE roster editing (Phase 7), VEC submission toggle (Phase 8), refund-request flagging (Phase 3)

**Unit Tests:** Each action's authorization check (Session Manager can only act on their own sessions), each action's state-transition correctness (reuses/extends the logic already unit-tested in its originating phase — this phase's tests focus on the UI-triggered wiring, not re-testing the underlying business logic).

**Deliverable:** A Session Manager test user can perform every listed action against a test session and see the results reflected correctly.

---

### Phase 9c — Admin Config Screens

**Goal:** Everything listed under SystemAdmin and TeamAdmin above. **Needs a real split when this phase is actually built** (not yet designed in detail) — SystemAdmin's screens are deployment-wide (team creation, TeamAdmin grants, VEC management since VECs are shared/global, PII retention window, ULS polling settings, global audit log/job run history), TeamAdmin's screens are scoped to their own team (that team's Zoom/Square/Discord/Email credentials, fee configuration for VECs their team works with, that team's email templates, granting SessionManager/TeamLead within their team). Decide during 9a's design pass whether these are genuinely separate screen sets or one screen set with SystemAdmin able to pick "which team" while TeamAdmin is locked to their own.

- User management (create/deactivate, role assignment, Team Lead-to-Session Manager/Team Admin assignment)
- System config: Zoom/Square/Discord credentials, SMTP settings (From/Reply-To) — now per-`Team` (multi-team foundation), ULS polling settings, PII retention window (deployment-wide, SystemAdmin only)
- VEC management (`Vec` CRUD, including `SupportsYouthProgram`) — shared/global, SystemAdmin only
- Fee configuration (`FeeConfiguration` CRUD per VEC)
- Email template management (edit Subject/Body per `EmailTemplate.Key`, with available placeholders shown per template) — now per-`Team`
- Global session visibility (SystemAdmin), audit log viewer, job run history/ops dashboard

**Unit Tests:** Authorization (SystemAdmin-only screens actually reject TeamAdmin and vice versa for out-of-team actions), correctness of each config write (e.g. a new `FeeConfiguration` doesn't retroactively change past sessions, a new `EmailTemplate` edit takes effect on the next send without a deploy).

**Deliverable:** A SystemAdmin test user can create a team and grant Team Admin; a TeamAdmin test user can manage their own team's settings, fees, and email templates within that team; both can view the audit log and job run history (SystemAdmin globally, TeamAdmin scoped to their team).

---

### Phase 9d — Public Privacy Page + Polish

**Goal:** The public-facing piece, plus whatever's left over from the Claude Design mockups that didn't fit naturally into 9a–9c.

- Public privacy policy page (e.g. `/privacy`), unauthenticated, disclosing what candidate PII is collected (Name, Email, FRN), why (session administration, FCC application tracking, payment processing), and the retention policy — reference the actual current `RetentionWindowDays` value dynamically rather than hardcoding it, so it stays accurate if the Admin changes the setting. This is the link referenced in Phase 4's confirmation email. Content should be reviewed by Mike before publishing — this phase builds the page/mechanism, not the final legal wording.
- Visual polish pass against the Claude Design mockups across every role view
- Any remaining UI gaps found while using 9a–9c in practice

**Deliverable:** Public privacy page live and linked correctly from the confirmation email; overall admin backend visually matches the approved mockups.

---

## Phase 10 — PII Purge Job

**Goal:** Null candidate PII fields after a configurable retention window — anchored to the relevant end-of-process date depending on outcome.

- Config value: retention window in days (Admin-configurable, single value used by both triggers below — no default assumed, set explicitly before first run)
- **Trigger A — passed candidates:** `LicenseGrantDateUtc` is set and `Today - LicenseGrantDateUtc >= RetentionWindowDays` and `PiiPurgedUtc` is null
- **Trigger B — failed candidates:** `ApplicationStatus = Failed` and `Today - Session.ScheduledStartUtc >= RetentionWindowDays` and `PiiPurgedUtc` is null (same window, anchored to the exam date instead of a license grant — there's no FCC process to track once a Session Manager has recorded a failing result, so there's no reason to hold the PII any longer than that same window). **`NotTested` is intentionally excluded from this job** — no-show/withdrawal PII is nulled immediately at the moment the Session Manager deletes the candidate (Phase 9), not on this scheduled window.
- Purge action (either trigger): **null the fields** (not delete the row) — `Name`, `Email`, `Frn`, `HasFelonyDisclosure`, plus any `PaymentLinkUrl`/`SquarePaymentReferenceId` on associated `Payment` rows; set `PiiPurgedUtc` to now
- Preserve: `CallSign`, `LicenseGrantDateUtc`, `ApplicationStatus`, `SessionId`, `Payment.Amount`/`Status`/`Reason` — non-PII fields needed for historical session/VE/financial stats remain intact
- Log the purge action to `AuditLog`, noting which trigger fired

**Unit Tests:** Test both trigger boundaries independently (exactly at threshold, one day before, one day after, for each of Trigger A and Trigger B). Verify only PII fields are nulled and non-PII fields (including `Payment` amounts/status) are untouched. Verify `PiiPurgedUtc` is set and the job doesn't reprocess already-purged candidates. Verify a candidate still in a non-terminal state (`Unmatched`/`Received`) is never purged regardless of how old the session is.

**Deliverable:** Test candidates covering both trigger paths (one passed-and-granted, one failed) each have PII fields correctly nulled at their respective thresholds, non-PII stats fields untouched, audit entries created.

---

## Backlog (not scoped into a phase yet)

- **VEC discount programs:** some VECs offer discount programs for candidates (structure varies by VEC, not standardized). Not designed yet — needs real examples from the VECs you work with before it can be scoped. Note it here so it isn't forgotten, revisit once requirements are clearer.
- **No-FRN batch export:** when candidates test without an FRN (e.g. during a federal shutdown, per VEC policy at the time), a future feature could export the list of `FrnMissingAtRegistration` candidates once their FRNs are collected, formatted for whatever batch submission process the VEC requires. Not needed until this scenario actually recurs — revisit then.

## Suggested order of attack

Phases 0–4 form the core "session lifecycle" pipeline and should be done in order. Phase 5 (FCC watcher) and Phase 3 (Square) are independent of each other and can be done in either order once Phase 1 exists. Phase 6 depends on both 3 and 5. Phases 7, 8, 10 are small and can slot in anytime after Phase 0. Phase 6.5 (multi-team foundation) should land before Phase 9 (admin backend) even though it isn't a hard dependency of any single sub-phase — Phase 9 is the one phase where retrofitting team-scoped authorization after the fact would be genuinely costly (redoing authz surface area is a real security-bug risk), so building it team-aware from 9a onward is cheaper than doing it single-tenant first. Phase 9 (admin backend) is split into four sub-phases (9a–9d) and should be tackled in that order — 9a first since it establishes the auth/scaffolding the other three build on. 9b and 9c are each easiest once most of the data model they surface is populated by real jobs (after Phase 6), but 9a could be scaffolded earlier if you want a UI to watch data land as you build the other phases.
