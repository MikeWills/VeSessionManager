# TODO

**Everything that used to live here is now a GitHub issue.**
→ <https://github.com/MikeWills/VeSessionManager/issues>

Migrated 2026-08-10. This file tracked operational follow-ups and untriaged requests alongside
GitHub issues, `docs/audit-2026-08-03-tasks.md`, and `docs/spec.md`'s Backlog — four places to look
for "what's left", which is three too many. File new work as an issue.

## Where each section went

| Was | Now |
|---|---|
| Square — live-verify unmatched-payment matching | [#183](https://github.com/MikeWills/VeSessionManager/issues/183) |
| Email/SMTP — credentials, `EmailSettings` placeholders, template copy, live test | [#181](https://github.com/MikeWills/VeSessionManager/issues/181) |
| Payment Reminders — live verification | [#182](https://github.com/MikeWills/VeSessionManager/issues/182) |
| Admin auth — OAuth apps, first prod SystemAdmin, real prod users | [#185](https://github.com/MikeWills/VeSessionManager/issues/185) |
| Deployment — beta server provisioning, key ring backup | [#184](https://github.com/MikeWills/VeSessionManager/issues/184) |
| Deferred — exam-result sync "final poll" question | [#186](https://github.com/MikeWills/VeSessionManager/issues/186) |
| Deferred — self-update notification | [#187](https://github.com/MikeWills/VeSessionManager/issues/187) |
| Delete a user that has no history | [#188](https://github.com/MikeWills/VeSessionManager/issues/188) |
| Code-review findings (`docs/audit-2026-08-03-tasks.md`) | [#156–#180](https://github.com/MikeWills/VeSessionManager/labels/audit-2026-08-03) |

Feature-request pointers that were listed here ([#63](https://github.com/MikeWills/VeSessionManager/issues/63),
[#64](https://github.com/MikeWills/VeSessionManager/issues/64),
[#65](https://github.com/MikeWills/VeSessionManager/issues/65)) were already issues and stay where
they are. #107 was listed as open here and had in fact closed on 2026-08-07 with the VE management
work — the kind of drift that comes free with a second list.

## Labels worth knowing

`ops` — configuration or server work, not code. `audit-2026-08-03` — from the six-agent review.
`needs-design` — open questions to settle before building. `security`, `tech-debt`, `bug`,
`enhancement`, `documentation` as usual.

## Still true, and not tracked as an issue

Square, Zoom, Discord and Email/SMTP are **optional integrations** — the app runs fine with any
subset unconfigured (one quiet log line per poll, no errors). Nothing in the issue list above blocks
further development; the `ops` items block *live end-to-end verification* of those integrations,
which is a different thing.
