# Post-Launch Security/Quality Hardening Pass (2026-07-21)

Findings from a full-codebase review, not a numbered spec phase.

## Cross-tenant IDOR (the real finding)

`Pages/SessionManager/Detail.cshtml.cs`'s `AuthorizeAsync()` only proved the acting user may edit
the *session* named by the page's own route `Id` — every candidate/payment action handler
(`OnPostMarkFailedAsync`, `OnPostDeleteCandidateAsync`, `OnPostMarkPaidAsync`, etc.) also takes a
separately-submitted `candidateId`/`paymentId` form value that was never checked to actually belong
to that session, so a Session Manager authorized for their own session could act on any
candidate/payment id in the database by editing the posted form value.

Fixed by adding `CandidateBelongsToSessionAsync`/`PaymentBelongsToSessionAsync` checks to every such
handler. **Any future per-candidate/per-payment POST handler on a session-scoped page needs the same
check** — the session-level `AuthorizeAsync()` alone is not enough. This is the same bug class
`EmailTemplates.cshtml.cs`/`FeeConfigurations.cshtml.cs`'s own "authorize against the entity's
actual owning team" comments already called out — it just hadn't been applied here.

## Other fixes in the same pass

- `UserManagementService.DeactivateAsync` now calls `userManager.UpdateSecurityStampAsync` —
  lockout alone only blocks *future* sign-ins; an already-issued auth cookie kept working until its
  own ~14-day expiry without this.
- `UserManagementService.SetManagerAsync` now validates the submitted `managerUserId` belongs to the
  same team as the TeamLead being assigned — previously a TeamAdmin could grant a TeamLead
  cross-team read access via `SessionAccessScope`'s `ManagedByUser.TeamId` resolution.
- `EmailTemplateRenderer.Substitute` now HTML-encodes placeholder values substituted into the (real
  HTML) email `Body`, not the plain-text `Subject` — several placeholders (`CandidateName`, etc.)
  are ExamTools registrant-controlled data, previously injected unescaped into HTML rendered by a
  recipient's mail client.
- `ExternalLoginCallback` now rejects a Google external sign-in whose `email_verified` claim is
  explicitly `false` before trusting an email-claim match enough to link/sign in (Google's claim is
  now explicitly mapped in `Program.cs` — `AddGoogle`'s own defaults don't include it — Microsoft's
  handler exposes no equivalent claim, so that path is unchanged).
- `VolunteerExaminerSyncService.RunAsync` now wraps each session's ExamTools call in try/catch and
  saves after each session — it was the one scan-based service missing the per-item isolation every
  other one already has, so one session's failure no longer skips every later session in that
  team's list nor discards reconciliation already done for earlier ones.
- `SessionActionService.MarkCompletedAsync`'s felony-disclosure email fan-out now isolates each
  candidate's send so one SMTP failure doesn't throw past an already-committed status flip.

See `CLAUDE.md`'s "Established Patterns" section for the shared helpers introduced during this pass
(`TerminalStatuses`, `AddAuditLog`, `CandidatePiiFields.Clear`, `ToEmailCredentials`,
`TryResolveManageableTeamId`) — use those instead of re-deriving the same logic anywhere new.
