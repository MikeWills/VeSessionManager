# Audit Task List — 2026-08-03

Source: six-agent deep audit (security, optimization/dead-code, and four traceability layers:
UI→handlers, handlers→services, services→EF/SQLite, Worker jobs→services→clients→DB).
Codebase at commit `2898817` + uncommitted footer/version diff.

**How to use this file (for humans and sub-agents):** each task is self-contained — problem,
files, fix, acceptance criteria. Pick a task, do exactly its scope, run `dotnet build` +
`dotnet test`, tick the checkbox. Tasks within a tier are independent unless a
**Depends/Pairs** line says otherwise. Follow CLAUDE.md conventions (Conventional Commits,
feature branches, per-item-save patterns are deliberate — don't "optimize" them away).

Overall verdict from all six agents: no Critical security findings; IDOR/CSRF/injection/secrets
handling all verified clean; schema matches migrations exactly. The defects below are real but
bounded.

---

## P0 — Critical (fix before beta users touch it)

- [x] **T01 — DONE (2026-08-03): fixed bare `GetUserAsync` in Detail/CandidateDetail POST authorization** — both `AuthorizeAsync` helpers now use `GetUserWithManagerAsync` with a comment explaining why; full build clean, 543 tests pass. The optional hardening (make `GetEffectiveTeamIds` throw on an unloaded `UserTeams`; switch the safe-but-bare admin call sites for uniformity) was NOT done — remains available as follow-up. Original finding below for reference.
  *(security High-adjacent; found independently by 3 agents)*
  - Files: `src/VeSessionManager.Web/Pages/SessionManager/Detail.cshtml.cs:305`, `CandidateDetail.cshtml.cs:137`
  - Problem: the shared `AuthorizeAsync()` used by all 14 (Detail) + 8 (CandidateDetail) POST handlers loads the user with bare `userManager.GetUserAsync`, so `user.UserTeams` is empty and `SessionAccessScope.CanEdit` returns false — **every write action 403s for TeamAdmin/SessionManager**. SystemAdmin short-circuits, which masked it during live verification. This is the exact CLAUDE.md Known Constraint; the GET paths were fixed, the POST helper wasn't.
  - Fix: replace with `userManager.GetUserWithManagerAsync(dbContext, User)` in both. Hardening (optional, recommended): make `SessionAccessScope.GetEffectiveTeamIds` throw if `UserTeams` was never loaded so this class of bug can't silently recur; also switch the safe-but-bare call sites (`Admin/SystemSettings.cshtml.cs:58,86`, `Admin/Teams.cshtml.cs:28`, `Admin/Vecs.cshtml.cs:53,71`, root `Index.cshtml.cs:22`) to the loader for one uniform rule.
  - Accept: log in as a TeamAdmin/SessionManager (not SystemAdmin) and confirm a POST action on both pages succeeds; add a test exercising `CanEdit` through the same load path.

- [x] **T02 — DONE (2026-08-03)** — `FirstName` now cleared; **reflection guard test** fails when a new `Candidate` field isn't explicitly classified as PII-or-retained; **self-healing repair pass** (`PiiPurgeService.RepairIncompletelyPurgedCandidatesAsync`) re-clears rows purged under the old definition, preserving the original `PiiPurgedUtc`, counted in `PiiPurgeResult.AlreadyPurgedCandidatesRepaired`. See `docs/pii-purge.md`. Original finding below. *(security HIGH H-1)*
  - Files: `src/VeSessionManager.Core/Entities/CandidatePiiFields.cs:13-26`, `Candidate.cs:18`
  - Problem: `Clear` nulls Name/Email/Frn/HasFelonyDisclosure but not `FirstName` (added later for `{{CandidateFirstName}}`). A scheduled-purged Granted/Failed candidate keeps their given name forever, and `CandidateDetail.cshtml.cs:215` renders it (it only hides FirstName for withdrawn candidates).
  - Fix: add `candidate.FirstName = null;` to `Clear`. Add a reflection-based unit test asserting every PII-classed property on `Candidate` is null after `Clear` so the next added field can't repeat this. Consider a one-off data fix for already-purged rows (`PiiPurgedUtc != null && FirstName != null`).
  - Accept: test passes; purged candidates show no first name.

- [x] **T03 — ~~FRN written verbatim into the permanent audit log~~ CLOSED by decision (2026-08-03)**
  - Mike's ruling: **FRN is not PII** — it's public FCC data, and having the value in the audit
    log is *useful* for tracking if there's ever a question about a correction. No scrub, no
    message change. (The security agent had flagged this as High on the assumption FRN was PII;
    that assumption is overruled.)
  - Follow-on decision folded into T19: `CandidatePiiFields.Clear` currently **nulls `Frn`** on
    purge — under this ruling that's optional, and keeping it would preserve tracking value.

- [x] **T04 — DONE (2026-08-06)** — sections mirrored into Web's `appsettings.json` **and**
  `appsettings.Production.json`. The base file mattered too: the drift starts there, so Development and
  Test were also resolving `examtools.dev` in Web while the Worker used `alpha.exam.tools` — fixing only
  Production would have left that. Verified by resolving the layered config for all three environments;
  Web and Worker now agree in each. Original finding below. *(optimization B4)*
  - Files: `src/VeSessionManager.Web/appsettings.Production.json` (and Test), cf. `SquareOptions.cs:8`, `ExamToolsOptions.cs:8`
  - Problem: Web binds and *uses* both option sets (retest payment links, manual candidate refresh), but no Web appsettings has the sections — prod Web silently falls back to `Square:Environment = "Sandbox"` and `ExamTools:BaseUrl = "https://examtools.dev"` while the Worker uses Production/`alpha.exam.tools`. A Web-initiated Square/ExamTools call in prod targets the wrong environment.
  - Fix: mirror the Worker's `Square`/`ExamTools` sections into Web's `appsettings.Production.json` (and Test where applicable). Verify no secrets involved (these sections are host/environment only — credentials are per-Team).
  - Accept: Web and Worker resolve identical Square environment and ExamTools base URL in each environment.

---

## P1 — High

- [x] **T05 — DONE (2026-08-03)** — hidden `false` sibling added after the checkbox (matching `SystemSettings.cshtml`), handler parameter now non-nullable `bool`, with comments on both explaining why the hidden input is load-bearing. Original finding below. *(UI trace #1)*
  - Files: `src/VeSessionManager.Web/Pages/Admin/TeamSettings.cshtml:111`, `TeamSettings.cshtml.cs` (`OnPostUpdateSmtpAsync`, `bool? useStartTls`), `TeamSettingsService.cs:120`
  - Problem: bare checkbox with no hidden `value="false"` sibling; unchecked posts nothing → binds null → stored null → `ToEmailCredentials` fallback treats null as true → the box re-checks itself. Implicit-TLS (465) / plain (25) setups can't be configured.
  - Fix: copy the correct pattern from `SystemSettings.cshtml:107-108` (hidden false sibling), make the parameter non-nullable `bool`, persist real false.
  - Accept: uncheck → save → reload shows unchecked; `SmtpUseStartTls == false` in DB.

- [x] **T06 — DONE (2026-08-03): youth-rate attestation now enforced server-side.** Explicit `if (!Input.ConfirmYouth)` check in `OnPostAsync`; `[Required]` kept for client-side only, with comments on both explaining why neither covers the other. See `docs/security-hardening-2026-08-03.md` §5. Original finding below.
  *(UI trace #2)*
  - File: `src/VeSessionManager.Web/Pages/Public/YouthConfirm.cshtml.cs:23`
  - Problem: `[Required]` on a non-nullable `bool` always passes (checkbox tag helper's hidden `false` satisfies it) — a JS-off or direct POST confirms the youth rate without the attestation. Client-side jQuery validation masks it (and is itself broken, see T09).
  - Fix: `[Range(typeof(bool), "true", "true")]` or explicit `if (!Input.ConfirmYouth) { ModelState.AddModelError(...); }` in the handler.
  - Accept: a POST with `ConfirmYouth=false` re-renders with an error and does not call `ConfirmAsync`.

- [x] **T07 — DONE (2026-08-03)** — key is cleared together with the standard-rate link it belongs to (a plain `??=` would have replayed the *standard* link), then `??=` generated and persisted before the Square call, so a crash-retry is an idempotent replay instead of a second live order. See `docs/youth-payment-confirmation.md`. Original finding below. *(DB trace §4)*
  - File: `src/VeSessionManager.Core/Payments/YouthPaymentConfirmationService.cs:122`
  - Problem: `payment.SquareIdempotencyKey = Guid.NewGuid().ToString();` is unconditional (the comment claims persist-once but the code isn't). Crash between Square's `CreatePaymentLink` success and `SaveChangesAsync` → candidate confirms again → second live Square order, first orphaned. Contrast the correct `PaymentGenerationService.cs:192` (`if (... is null)`).
  - Fix: null the key in the same save that deletes the old standard link (a plain `??=` would replay the *standard* link), then `??=` + persist **before** calling Square.
  - Accept: unit test simulating retry-after-crash reuses the same key; only one Square call would be minted.

- [x] **T08 — DONE (2026-08-03)** — filtered unique index on `Payments (CandidateId, Reason)` (Retest excluded, filter built from the enum value not a literal `0`); creation now saves **per candidate** so one collision can't roll back the pass, catching `DbUpdateException`, detaching the failed entity and counting `PaymentsSkippedAlreadyExisted`. Migration deletes only **provably inert** duplicates (Unpaid, never linked, never paid) and fails loudly on any that were linked/paid — deliberate, since that case means money may have moved twice. Verified against the real dev DB. See `docs/worker-resilience.md`. Original finding below. *(DB trace §6)*
  - Files: `src/VeSessionManager.Core/Data/AppDbContext.cs` (Payment config), `PaymentGenerationService.cs:48-56`
  - Problem: Web "Refresh now" runs the full pipeline concurrently with the Worker tick; both can evaluate `!c.Payments.Any(p => p.Reason == InitialExam)` before either saves. No unique index on `(CandidateId, Reason)` → two Unpaid rows → two live Square links → duplicate reminders.
  - Fix: filtered unique index `HasIndex(p => new { p.CandidateId, p.Reason }).IsUnique().HasFilter("Reason = 0")` (Retest legitimately repeats) + migration; the race becomes a caught constraint violation retried next tick — verify PaymentGenerationService tolerates the throw (per-item save pattern should already).
  - Pairs: T20 (structural serialization) removes the sibling email/Zoom/Discord races too.
  - Accept: migration applies cleanly up/down; concurrent-create test (real SQLite `:memory:`, not InMemory — see `VecExamToolsCodeSqliteTests` pattern) shows second insert throws.

- [ ] **T09 — jQuery never loaded under `_PublicLayout`; client-side validation dead** *(optimization B1; severity lowered by T06)* — server-side validation is now authoritative on the page this most mattered for (T06 made the youth attestation a real handler check), so this is a UX gap rather than a hole. Note the dependency: the `[Required]` left on `YouthConfirm`'s checkbox does nothing until this is fixed, and its comment says so.
  - Files: `src/VeSessionManager.Web/Pages/Shared/_PublicLayout.cshtml:31`, `_ValidationScriptsPartial.cshtml:1` (pages: Login, ForgotPassword, ResetPassword, YouthConfirm)
  - Problem: the partial loads `jquery.validate` + unobtrusive but `_PublicLayout` never loads jQuery — `jQuery is not defined`, validation never runs (server side catches errors, masking it).
  - Fix: add the jQuery script to `_PublicLayout`'s scripts pipeline (self-hosted under `wwwroot/lib/`, already present) before the partial's scripts.
  - Accept: browser console clean on Login; leaving a required field empty blocks submit client-side.

- [x] **T10 — DONE (2026-08-03)** — new `JobTick.GuardedAsync` wraps every job's tick body (two tick-level `continue`s became `return`, which lambdas require); `JobRunHistoryLogger` protects both its saves and clears the change tracker on the failure path so a poisoned entity isn't retried by its own `finally`. Smoke-tested: Worker starts, all jobs tick, zero guard messages. See `docs/worker-resilience.md`. Original finding below. *(Worker trace defect 2)*
  - Files: `src/VeSessionManager.Core/Jobs/JobRunHistoryLogger.cs:31,49-53`; job loops `SessionIngestionJob.cs:70-72,116-117`, `PerTeamDailyJob.cs:36`, `UlsWatcherJob.cs:37-45`, `HistoricalImportJob.cs:41`
  - Problem: default `BackgroundServiceExceptionBehavior.StopHost` — a transient `SQLITE_BUSY` in the pre-logger queries, the logger's own start-row save, or a poisoned tracked entity rethrowing in the logger's finally-save escapes `ExecuteAsync` and kills every job (the documented Square-constructor incident class, via a new path).
  - Fix: wrap each timer-tick body in `try/catch (Exception ex) when (ex is not OperationCanceledException)` + LogError; in `JobRunHistoryLogger` move the start-row save inside try and `ChangeTracker.Clear()` (re-attaching the history row) — or use a dedicated short-lived context for history rows — before the final save. Bonus: stop recording shutdown cancellation as a job failure (`:43-48`).
  - Accept: a job body throwing (test with a fake) logs and the host keeps running; next tick proceeds.

- [x] **T11 — DONE (2026-08-03)** — `HasPendingAsync` and `RunNextPendingAsync` both now also select requests `Running` longer than `StaleRunningThreshold` (30 min), and the reclaim **resumes at the interrupted chunk** via `Chunks(...).Skip(ChunksCompleted)` rather than re-walking the range (which would re-fetch every earlier chunk and push progress past `ChunksTotal`). See `docs/historical-import.md`. Original finding below. *(Worker trace defect 1)*
  - File: `src/VeSessionManager.Core/Ingestion/HistoricalImportService.cs:90-156` (esp. `:94,:103,:57-60`)
  - Problem: request flips to `Running`; only `Pending` is ever selected again; graceful shutdown/crash mid-import leaves it `Running` forever and the one-at-a-time guard blocks all future imports for that team. Only recovery today is hand-editing the DB.
  - Fix: at Worker startup reset stale `Running` → `Pending` (re-running a range is documented idempotent), or select `Pending || (Running && StartedUtc < now - threshold)`.
  - Accept: test — a `Running` row older than threshold gets picked up/reset and completes.

- [ ] **T12 — Data Protection key ring lives beside the SQLite DB** *(security M-1; deployment change)*
  - Files: `src/VeSessionManager.Web/Program.cs:44-46`, `Worker/Program.cs:40-42`, both `appsettings.Production.json`, `docs/deployment.md`, deploy workflow/server
  - Problem: `/var/lib/vesessionmanager/dataprotection-keys` (plaintext XML keys, no `ProtectKeysWith*`) sits in the same directory as the DB — any leaked backup/disk grabs ciphertext + key together, defeating `EncryptedStringConverter`'s entire threat model.
  - Fix: move the key ring to a separate directory with distinct ownership/mode 0700 (e.g. `/var/lib/vesessionmanager-keys/`), exclude it from the DB backup bundle (back it up separately — losing it is unrecoverable per CLAUDE.md), or `ProtectKeysWithCertificate`. Update docs/deployment.md and the config value; coordinate the move so existing keys are copied first.
  - Accept: keys readable by both processes at the new path; old path empty; backup discipline documented.

---

## P2 — Medium: security hardening (mostly `Web/Program.cs`)

- [x] **T13 — DONE (2026-08-03)** — headers middleware added; CSP allowances verified against real markup (Google Fonts + ~139 inline styles), runtime-verified. See `docs/security-hardening-2026-08-03.md` §3. *(M-2)* — add middleware before `UseRouting`: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: same-origin`, CSP `default-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://*.squareup.com` (YouthConfirm redirects to Square; check inline `style=` in TeamSettings.cshtml before a strict `style-src`). Accept: headers present on every response; all pages render unbroken.
- [x] **T14 — DONE (2026-08-03)** — global limiter, 20/min per IP on `/Account/*`, no-limiter partition elsewhere; **required adding `UseForwardedHeaders`** or the whole internet would share one bucket behind the Apache proxy. Runtime-verified (19×200 then 429s; `/Privacy` unaffected). See `docs/security-hardening-2026-08-03.md` §1-2. *(M-3, L-5)* — .NET built-in rate limiter partitioned by remote IP (~10/min on `/Account/*`); keep Identity lockout and the per-user reset throttle as second layers. Accept: 11th request in a minute from one IP gets 429; normal login unaffected.
- [ ] **T15 — No fallback authorization policy** *(M-7)* — `FallbackPolicy = RequireAuthenticatedUser`; add `[AllowAnonymous]` to the public pages (Login, ForgotPassword(+Confirmation), ResetPassword, YouthConfirm, Privacy, Error, root Index, AccessDenied, ExternalLoginCallback as applicable). Accept: a new page with no attribute now challenges; all public pages still anonymous; Square webhook endpoint unaffected.
- [ ] **T16 — Auth cookie defaults to 14-day sliding** *(M-8)* — `ExpireTimeSpan = 8h`, `SlidingExpiration = true`; consider `SameSite=Strict` (verify the Square return + external OAuth login flows still work — OAuth correlation cookies may need Lax).
- [x] **T17 — DONE (2026-08-03)** — reset link now built from `App:PublicBaseUrl` (never the request host), plus `AllowedHosts` pinned in Production. **Deployment caveat: a box served under any other hostname 400s until `AllowedHosts`/`App:PublicBaseUrl` are updated.** See `docs/security-hardening-2026-08-03.md` §4. *(M-9)* — pin `AllowedHosts` in `appsettings.Production.json`; build the reset URL from `AppOptions.PublicBaseUrl` (pattern already at `CandidateNotificationService.cs:418`) instead of `Request.Scheme`/host at `ForgotPassword.cshtml.cs:45-46`. Accept: reset email link host is the configured public base regardless of request Host header.
- [ ] **T18 — `EncryptedStringConverter` silently accepts tampered/undecryptable ciphertext as plaintext** *(M-6)* — `Data/EncryptedStringConverter.cs:39-52`: now that `--migrate-team-secrets` has run everywhere, retire the legacy fallback (log Error + return null = "not configured"), or gate behind a default-off config flag; at minimum LogWarning in the catch so key-ring drift is visible. Accept: a garbage ciphertext produces a logged warning/error, never a silently-used raw value.
- [x] **T19 — DONE (2026-08-03): purge keeps the FRN; privacy page + docs aligned** *(was M-5)* — Mike's ruling: FRN (and CallSign/`FccUlsLicenseKey`/`UlsApplicationFileNumber` — all public FCC data) is **not PII**; keep it for traceability. Implemented: `CandidatePiiFields.Clear` no longer nulls `Frn` (doc comment records the decision); the three test assertions flipped to assert retention; `Privacy.cshtml` no longer lists FRN among purged fields and now names the retained public-FCC identifiers; `docs/pii-purge.md`'s preserved-field list updated. All 543 tests pass.
- [ ] **T20 — Serialize per-team pipeline runs / throttle Detail's Refresh** *(L-3 + DB trace §6 structural; PARTIALLY MITIGATED 2026-08-03)* — the Detail button is now **session-scoped** (`ManualCandidateRefreshService.RunForSessionAsync`, see `docs/team-maintenance.md`), so a Web-side run races the Worker only on one session's rows and can no longer fan out emails/links team-wide. Remaining: the button is still unthrottled (`Detail.cshtml.cs` `OnPostRefreshCandidatesAsync`), the one-session race window still exists until T08's unique index lands, and SQLite WAL (`PRAGMA journal_mode=WAL`) is still worth enabling for the two-writer setup. Options unchanged: per-session debounce, and/or route refreshes through a Worker-consumed request row (`HistoricalImportRequest` pattern). Accept: per design chosen.
- [ ] **T21 — Schema hygiene migration bundle** *(DB trace §1/§7 + perf P2/P3/P8/P9)* — one migration:
  - `Session.RetainedAmountOverrideByUser` FK → explicit `OnDelete(Restrict)` (currently ClientSetNull, contradicting the file's stated invariant, `AppDbContext.cs:64-72`); `HasPrecision(10,2)` on `RetainedAmountOverride`.
  - Pin explicit numeric values on all persisted enums (`Entities/Enums.cs` + `HistoricalImportStatus`) — a mid-list insertion today silently renumbers stored rows.
  - Indexes: `Session (TeamId, ScheduledStartUtc)`; `Payment.SquarePaymentReferenceId` (webhook path is a full scan under Square's response deadline); `AuditLog.TimestampUtc`; `JobRunHistory (TeamId, JobName, StartedUtc)`; `Candidate.ApplicationStatus`.
  - Accept: `dotnet ef migrations has-pending-model-changes` clean afterwards; documented down-path.

---

## P3 — Medium: UI/traceability fixes

- [ ] **T22 — Admin Users + Unmatched Payments: empty manager dropdown & team-filter loss on POST** *(UI #3/#4, L2 D3; PARTIALLY FIXED 2026-08-04)* — the empty "Assign manager" dropdown is resolved **on single-team deployments only**: `TryResolveManageableTeamId` now auto-selects when exactly one team exists, so `effectiveTeamId` is no longer null there. **Still open** for a SystemAdmin on "All teams" with two or more teams (the original bug), and the team-filter-loss-on-POST half is untouched. — `Users.cshtml.cs:81-84`: `ut.TeamId == effectiveTeamId` with null (SystemAdmin "All teams") matches nothing → "Assign manager" always "(none)"; branch on null. All 7 Users POST forms + UnmatchedPayments Match form lack a hidden `teamId`, and `asp-page-handler` drops the query string, so redirects bounce to "All teams" — add hidden `teamId` (EmailTemplates.cshtml is the in-repo model). Accept: manager assignable from All-teams view; POST actions preserve the team filter.
- [ ] **T23 — Sessions filter form wipes remembered column sort** *(UI #6)* — `Index.cshtml:16` / `Index.cshtml.cs:208-210`: add hidden `sort`/`dir` inputs to the filter form so applying a filter doesn't save `Sort=""` over the cookie.
- [ ] **T24 — `.pill-count` unstyled inside filter dropdowns** *(UI #5)* — widen `app.css:99-100` selector to cover `.menu label.item .pill-count` (ApplicantStatus:29,35; UnmatchedPayments:26,32).
- [ ] **T25 — Callsign normalization helper + Bootstrap fix** *(opt B3+U8)* — new `Core/CallSign.Normalize(string?)` = `Trim().ToUpperInvariant()`; adopt at the 6 sites (`UserManagementService.cs:95`, `ExamToolsUlsLookupClient.cs:85`, `VolunteerExaminerRosterService.cs:33`, `VolunteerExaminerSyncService.cs:126,149`, `BootstrapAdminCommand.cs:74` — the last currently misses `.Trim()`, a real bug).

---

## P4 — Refactors, performance, dead code (value-ordered; no behavior change intended)

- [ ] **T26 — `Session.IsCompleted` / `CompletedUtc` helpers** *(opt U1, High value)* — the "finished" invariant lives in change-together comments at `Index.cshtml.cs:272,347,602`, `Detail.cshtml.cs:393`, `VolunteerExaminerSyncService.cs:91`. Add EF-translatable members on `Session`; leave the ingestion cancellation predicate alone (different rule).
- [ ] **T27 — `TeamEmailDispatcher`** *(opt U2, High value)* — collapse the 7× render→send→stamp flow across `CandidateNotificationService` (5 methods) + `PaymentReminderService` (2); dedupe the byte-identical `FormatSessionDate` and the 3 verbatim `EmailMessage` constructions; hoist the per-candidate template re-fetch (`EmailTemplateRenderer.RenderAsync`) out of loops. Do NOT fold in `PasswordResetService` (system creds/throttle) or `YouthPaymentConfirmationService`. ~160→~50 lines.
- [ ] **T28 — `CandidatePresentation` helper** *(opt U3, High value — drift already happened)* — Detail vs CandidateDetail duplicate isWithdrawn/PII-name-fallback/FRN-ladder (texts already diverged)/status labels (3 spellings of NotTested)/8 capability flags. One static class (pattern: `LicenseClassFormatter`). Do together with the `LoadForDisplayAsync` split (S4).
- [ ] **T29 — Sessions list: stop `Include(Candidates)` for a count** *(opt P1 — biggest query win)* — `Index.cshtml.cs:254,626`: project `CandidateCount = s.Candidates.Count` + the ~10 scalar columns `ToRow` needs instead of materializing every candidate row of up to 100 sessions.
- [ ] **T30 — `AsNoTracking` + query-shape pass** *(opt hygiene)* — 13 display-only sites (only `Privacy.cshtml.cs:13` has it today). Priority: `ApplicantStatus.cshtml.cs:103-110` (unbounded, `Include(Payments)` for one bool — project it), `Detail.cshtml.cs:351-357` (5-include graph — add `AsSplitQuery`), `UnmatchedPayments` (unbounded — add a Take), `IngestionStatusService.cs:66` (tracked full Teams incl. credential decryption every 60s). Do NOT blanket-default NoTracking (POST handlers share the scoped context).
- [ ] **T31 — `Team.ToSquareCredentials()` / `ToZoomCredentials()`** *(opt U4)* — replaces 5 + 2 hand-built constructions (`PaymentGenerationService.cs:108,167`, `SquarePaymentLinkPurgeService.cs:54`, `SquarePaymentMatchingService.cs:195`, `YouthPaymentConfirmationService.cs:101`; `SessionEventSchedulingService.cs:212,343`), same pattern as `ToEmailCredentials`.
- [ ] **T32 — `MoneyFormatter.Usd` + `ChipFormatter`** *(opt U5/U6)* — money formatted 18 ways in 3 spellings; payment/VEC chip switches duplicated across Detail/CandidateDetail/Index.
- [ ] **T33 — Route 4 inline `new AuditLog` sites through `AddAuditLog`** *(opt U7, trivial)* — `SystemSettingsService.cs:79-89`, `SessionIngestionService.cs:524-532`, `SessionEventSchedulingService.cs:345-353,369-377`.
- [ ] **T34 — Split the two giant methods** *(opt S1/S2)* — `SessionIngestionService.RunAsync` (181 lines → 4 phase methods matching its own comment blocks; keep `ImportHistoricalRangeAsync` separate per documented invariant) and `IndexModel.OnGetAsync` (114 lines → filter/summary/query helpers; natural to combine with T26+T29). Optional follow-ons: `SessionEventSchedulingService` loop/halves (S3), `PaymentGenerationService.RunAsync` (S4).
- [ ] **T35 — Small perf batch** *(opt P4-P7)* — memoize current user in `HttpContext.Items` (loaded 2-3×/request via `_AppLayout:11,21-31`); `SessionAccessScope.GetAvailableTeamsAsync:157` project `(Id,Name)` instead of whole Teams incl. encrypted-column decryption; `HistoricalImportService:116,192` preload known-session dictionary (N+1 on re-import); `FeeConfigurations.cshtml.cs:63,67` correlated `Any()` instead of loading every `Session.FeeConfigurationId`.
- [ ] **T36 — Dead-code removal** *(opt D1-D8, all grep-verified)* —
  - `AdminAccessScope.CanAccessVecManagement/CanAccessSystemSettings/CanCreateTeam` (test-only; pages gate via `[Authorize(Roles)]`) + their tests.
  - `Vec.MatchCode` (zero production reads; the doc comment at `Vec.cs:14` is actively misleading — fix it if keeping).
  - `UlsLookupResult.PreviousOperatorClass` (+ wire `PrevLicenseClass`), `UlsHistoryEntry.LogDateUtc`, `.btn-ghost` CSS.
  - Bootstrap chassis: switch `Error.cshtml` to `_PublicLayout`, then delete `_Layout.cshtml`, `_LoginPartial.cshtml`, `site.css`, `site.js` (empty stub), `wwwroot/lib/bootstrap` — keep jquery + jquery-validation (needed by T09).
  - Tighten to private: `HistoricalImportService.CountChunks`, `CandidateEmailHistoryFormatter.FormatSentUtc`, `UlsWatcherJob.LatestDueSlotUtc`, `UlsLookupMapper.Map`.
  - Decide (don't leave ambiguous): write-only columns `Session.CancelledUtc/RescheduleFlaggedUtc/RetainedAmountOverrideUtc/ByUserId`, `Payment.RefundRequestedUtc` — keep as audit trail (document) or drop (migration). Removing the unconfigured `RetainedAmountOverrideByUser` nav property is safe either way (but see T21 first).
- [ ] **T37 — Low-severity security batch** *(L-6, L-8, L-10 remain; **L-2 DONE 2026-08-03** — webhook capped at 64KB via Content-Length check + `MaxRequestBodySize`, runtime-verified 413/401, see `docs/security-hardening-2026-08-03.md` §6)* — hoist the `"Microsoft"` external-login allowlist to a `static readonly` beside the provider registrations (`ExternalLoginCallback.cshtml.cs:56`); `Cache-Control: no-store` on TeamSettings; cap historical-import span (e.g. 3 years, `HistoricalImportService.cs:40-52`).
- [ ] **T38 — Worker polish** *(Worker trace minors)* — reject unknown `--` CLI switches with a listing + non-zero exit (`Worker/Program.cs:128-154`); don't record shutdown cancellation as a job failure (`JobRunHistoryLogger.cs:43-48`, if not already done in T10); `UlsWatcherService.cs:53-54` use `!TerminalStatuses.Contains(...)` instead of restating the complement.
- [ ] **T39 — Consistency cosmetics** *(L2 observations, opt B5)* — unify stale-auth-cookie handling (4 pages throw `InvalidOperationException`/500, others `Forbid()`); fix unreachable-enum-arm messages (`Detail.cshtml.cs:124` SessionNotFound→"already marked submitted", `Vecs.cshtml.cs:64` create-path NotFound→"already exists"); `ClearRescheduleFlagAsync` also clear `RescheduleFlaggedUtc` (`SessionActionService.cs:147`); remove stale "walk-in" comment (`app.js:5`); FeeConfigurations dead "+ New" button edge (`FeeConfigurations.cshtml:14-17`).

---

## Verified clean (so nobody re-audits these)

- **Security:** zero raw SQL; zero `Html.Raw`; email placeholders HTML-encoded (subject deliberately not); MimeKit blocks header injection; IDOR ownership re-checks present on every id-taking POST handler; CSRF correct incl. all four explicit-action forms; no secrets in config; password-reset flow non-disclosing/throttled/single-use; TLS validation intact; no SSRF; no open redirects; Worker exposes nothing; no mass-assignment.
- **Traceability:** all ~60 forms/links/modals/JS hooks resolve with matching names+types (except items above); all service signatures/arg orders verified by parameter name; all result-enum members handled; DI complete in both hosts, no scoped-into-singleton captures; entity model matches migrations exactly (`has-pending-model-changes` clean); all EF-translatability, DateTime-Kind and null-semantics traps handled at every current call site; encrypted columns never queried; `JobRunHistoryLogger` teamId correct at every call site; Eastern-time scheduling correct; optional-integration gates + aggregate-settled rule correct everywhere; Zoom/Discord/Square idempotency correct (except T07).
- **Deliberate patterns confirmed, don't "fix":** per-item `SaveChangesAsync` in scan jobs (12 sites); in-memory `HasEnded` filters (coarse query bound present); materialize-before-OrderBy (InMemory constraint); `_TestModeBanner` uncached read; per-item email send window (no outbox by design).
