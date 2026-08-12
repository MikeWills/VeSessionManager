# Action outcomes and candidate capabilities

*Issues [#304](https://github.com/MikeWills/VeSessionManager/issues/304) (DUP-01),
[#244](https://github.com/MikeWills/VeSessionManager/issues/244) (T-13),
[#274](https://github.com/MikeWills/VeSessionManager/issues/274) (T-43), 2026-08-11.*

Two new types in `src/VeSessionManager.Web/`:

| File | Owns |
|---|---|
| `ActionOutcomes.cs` | The mapping from a Core service result to the sentence the user is shown |
| `CandidateCapabilities.cs` | Which candidate actions are *applicable*, so a button is only rendered when it can succeed |

## What was wrong

Nine candidate actions were written out once on `Detail.cshtml.cs` and again on
`CandidateDetail.cshtml.cs`, and four session actions once on `Detail` and again on the session list
(`Index.cshtml.cs`). The audit diffed them and found them identical down to the punctuation.

**Identical is where these start. It is not where they stay.** Both drifts had already happened, in
these same files:

- **#244** — `VecSubmissionService.MarkSubmittedAsync` returns three values. `Detail` was fixed to
  handle all three, with a comment explaining why. The list copy was never touched, so it tested
  `result == Marked` and used one error string for the other two: a session that *could not be found*
  reported that it was **already marked submitted**. Not merely unhelpful — it asserts the action was
  unnecessary when in fact it did not happen.
- **#274** — `CanSendYouthProgram` was `!isWithdrawn && vec.SupportsYouthProgram` on
  `CandidateDetail` and `!isWithdrawn` on `Detail`. So the session roster offered the button for
  every VEC, and `CandidateNotificationService` refused it with
  `VecDoesNotSupportYouthProgram` — which the page then interpolated into the error message, raw enum
  name and all. A button whose only possible outcome was an error message naming an internal enum.

Same shape, both times: **one rule, two copies, one of them knowing something the other did not.**

## What is shared, and what deliberately is not

**Shared: the result → sentence mapping, and the capability rules.** Those are what drifted.

**Not shared: the handlers themselves.** Each is ~4 lines of authorization, ownership re-check and
redirect, and all three of those genuinely differ per page:

- the session list re-resolves a **posted** session id, because the list spans teams and a rendered
  control is never proof of rights;
- `Detail` trusts its route id for the session and re-checks that the posted `candidateId` belongs
  to it (the IDOR guard);
- `CandidateDetail` *is* the candidate, so its id is the route.

Collapsing those into one helper would have traded a real duplication for a fake abstraction, and the
authorization differences are exactly the part that should stay legible at each call site.

`CandidateCapabilities.For` **takes** `vecSupportsYouthProgram` and `hasAnyPayment` rather than
reading them off the entity. The two callers load them differently — `Detail` has one `Session` with
its `Vec` included and maps many candidates against it, `CandidateDetail` includes it per candidate —
and taking them as parameters makes the requirement visible at both call sites instead of depending
on an `Include` thirty lines away. Depending on that `Include` is how one copy came to omit the check
entirely.

One capability stayed inline, and is named in a test so it cannot grow neighbours: `CanMarkPaid` on
the roster acts on the row's single primary payment, where the detail page offers it per payment.

## Improvements that came with the move

Once each mapping had one home, being exhaustive over its enum cost nothing:

- `MarkCompleted` distinguishes `AlreadyDone` from `NotFound` (both page models said
  "Could not mark session completed." for either).
- `ClearRescheduleFlag`, `DeleteSession` and `SetRetainedAmountOverride` each got their `NotFound`
  branch.
- **The email actions no longer show the user a raw enum name.** `CandidateEmailSendResult` has
  seven values and all three email handlers rendered them with `$"…: {result}."`, so
  `NoEmailAddress`, `TemplateMissing` and `EmailNotConfigured` all reached a Session Manager verbatim.
  Each has a sentence now.

## Tests

| Test | Guards |
|---|---|
| `ActionMessageSingleSourceTests.NoActionMessageIsWrittenInMoreThanOnePlace` | 19 known-copied messages each have exactly one home in `src/VeSessionManager.Web` |
| `…CapabilityRulesAreNotComputedInsidePageModels` | No page model computes an `isWithdrawn &&` capability clause — except the one named above |
| `…EveryMessageInTheListActuallyExistsSomewhereInTheApp` | The list above is not silently checking nothing after a rename |
| `ActionOutcomesTests.MarkSubmittedToVec_TellsTheThreeOutcomesApart` | #244 itself |
| `…MarkCompleted_TellsAlreadyDoneApartFromNotFound` | The collapse the audit flagged beside it |
| `…NoFailureBorrowsASuccessSentence` | Every mapping, every enum value: a failure may not wear a success's words |
| `…NoOutcomeShowsTheUserARawEnumName` | The `$"…: {result}."` habit cannot come back |
| `CandidateCapabilitiesTests` | #274, plus each of the eight flags and the withdrawn-candidate clause they share |

**The single-source test is the one with teeth, and it was written first** — it reported 19 offenders
against the pre-refactor code across the three page models. All three fixes were then re-checked by
reverting them and confirming the relevant test fails: collapsing `SessionNotFound` back into
`AlreadySubmitted`, dropping the VEC check from `CanSendYouthProgram`, and restoring a duplicated
message each failed exactly one test and no others.

## The general lesson

A behavioural test of either copy in isolation cannot see this bug class. Two page models producing
the same message is the *normal* state right up until someone fixes one of them — the defect exists
only in the gap between the two edits. What is checkable is the property that makes the gap
impossible: **one string, one home.**
