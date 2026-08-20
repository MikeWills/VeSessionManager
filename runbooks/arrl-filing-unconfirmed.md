# Runbook — An ARRL filing came back `Unknown`

**When:** "Submit to VEC" was pressed for an ARRL session and the result was not a confirmed
success — a timeout, an unrecognized response, or the nav bell showing an unconfirmed submission.

**Why it works this way:** [`docs/arrl-vec-submission.md`](../docs/arrl-vec-submission.md).

---

## ⚠️ Do not press it again

**Absence of a receipt is not absence of a filing.** This is a fire-and-forget form POST: there is
nothing to query before creating and no idempotency key ARRL would honour, so a timeout *after the
request left the machine* may well mean it succeeded.

ARRL **cannot dedupe and has no unsend.** Whatever guards that button is the entire protection, and
one of the two guards is the existence of any submission row — including this unconfirmed one.

Telling somebody it failed is what produces a duplicate. The UI deliberately says "this may or may
not have been filed".

## What `Unknown` actually means

The **only** positive signal is the filename we posted echoed back, followed by
`has been uploaded successfully`. Everything else is `Unknown`.

- **Status codes are not consulted at all** — both outcomes arrive on the same endpoint.
- Nobody on this team has ever seen ARRL's failure page. A matcher built from zero samples would
  guess, and would guess in the expensive direction: a real rejection marked `Submitted` and never
  filed.
- **A two-file submission (youth form attached) may come back `Unknown` even when it worked** —
  whether two files produce two success lines is unverified, and every posted filename must be
  confirmed.

So `Unknown` is "a human needs to look", not "it failed".

## Steps

1. **Leave the session unsubmitted.** That is the correct state and it is what keeps the alert
   visible. Do not mark it submitted to clear the alert.
2. **Look at the stored response.** The submission row keeps `ResponseBody` — the actual page ARRL
   returned. If it contains the posted filename and `has been uploaded successfully`, the filing
   landed and the matcher simply did not recognize it (most likely the two-file case). Note this;
   it is a real sample and the only way that matcher ever improves.
3. **Telephone ARRL** and ask whether the session was received. This is the only resolution — there
   is no API and no status page.
4. **Once ARRL confirms it was received:** mark the session submitted in the app. There is no
   separate "resolved" flag, because that action already means it. This clears the bell alert.
5. **If ARRL confirms it was *not* received:** submit again from the preview. The preview is the
   only route to the POST, so it cannot be skipped.

## The alert

An unconfirmed submission raises an alert on the nav **bell** — the session still looks unsubmitted,
which is correct, but nothing else anywhere would make anyone go and look. The alert says
"may still have been filed", never "failed", and a test asserts that wording.

It is visible to Session Managers, not only admins — the role gate is per alert source, because this
one points at session detail, which every role can open.

## Related checks

- **The archive.** `/var/lib/vesessionmanager/vec-archives` holds what was sent, filed
  `team/vec/year/month`. It is the record for "there was a question about this session later" — the
  reason the feature exists. It is in the off-box backup — restore it alongside the database and
  key ring.
- **Non-ARRL sessions.** Every VEC has its own process; a session under any other VEC finds no
  submitter and is told so. That is the design, not a missing feature.
- **The Worker deliberately cannot reach ARRL at all.** Nothing files automatically, ever. If a
  filing appears to have happened without somebody pressing the button, that is a bug worth an issue.
- **`ToggleVecSubmission` is a misnomer** — it calls `MarkSubmittedAsync`, which refuses when already
  submitted. It has always been one-way.

## Rollout posture

While this is new, run it **alongside the manual process**: open the preview, compare it against
what would be filed by hand, and only then start trusting it. There is no sandbox and no dry-run —
that comparison is the entire substitute for testing that cannot exist here.
