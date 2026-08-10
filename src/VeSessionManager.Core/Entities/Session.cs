namespace VeSessionManager.Core.Entities;

public class Session
{
    public int Id { get; set; }

    /// <summary>External reference into ExamTools/HamStudy.</summary>
    public required string ExamToolsSessionId { get; set; }

    public required string Title { get; set; }

    /// <summary>ExamTools' own short lead-VE-callsign code (sessionDef.extId, e.g. "KM6Z - W5CBW" or
    /// "AD2GX") — the parenthetical text ExamTools' own calendar UI shows next to the team name.
    /// Meaningful to a human in a way ExamToolsSessionId's raw Mongo id never is; used alongside
    /// Title for the session list and breadcrumbs. Set once at ingestion, same "not re-synced later"
    /// precedent as Title itself — null on sessions ingested before this field existed.</summary>
    public string? ExtId { get; set; }

    public DateTime ScheduledStartUtc { get; set; }

    /// <summary>From ExamTools' sessionDef.duration (seconds), converted at ingestion time. Not in the original shared data model — added in Phase 2 because both the Zoom meeting and the Discord event require an explicit length/end time.</summary>
    public int DurationMinutes { get; set; }

    // Populated once Phase 2 creates the Zoom meeting/Discord event.
    public string? ZoomMeetingId { get; set; }
    public string? ZoomJoinUrl { get; set; }
    public string? DiscordEventId { get; set; }

    /// <summary>The ScheduledStartUtc value last successfully pushed to *both* Zoom and Discord. Null means never synced (a brand-new session). Mismatching ScheduledStartUtc is exactly the "needs Zoom/Discord create-or-update" signal Phase 2's scheduling job scans for — no separate event queue needed.</summary>
    public DateTime? ZoomDiscordSyncedStartUtc { get; set; }

    /// <summary>Denormalized copy for easy filtering/reporting without joining through FeeConfiguration.</summary>
    public int VecId { get; set; }
    public Vec Vec { get; set; } = null!;

    /// <summary>Not in the original shared data model — added as a multi-team foundation. Which team operationally ran this session (owns its ExamTools/Zoom/Discord/Square credentials) — independent of VecId (which VEC/fee schedule applies). See Team's own doc comment for why these are separate, unrelated FKs.</summary>
    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>Snapshot of whichever config was active when the session was created, so historical sessions keep an accurate fee record even after rates change.</summary>
    public int FeeConfigurationId { get; set; }
    public FeeConfiguration FeeConfiguration { get; set; } = null!;

    /// <summary>
    /// Flat TOTAL dollar amount this whole session retains, overriding the default per-candidate
    /// FeeConfiguration.RetainedAmount x candidate-count math entirely. A VE team may only keep
    /// enough to cover its real expenses (capped at RetainedAmount per candidate) — most sessions'
    /// real costs (pencils, paper, postage) are a fixed session-level expense, not a per-candidate
    /// one, so a team with $20 of real expenses and 50 candidates wants to retain $20 total, not
    /// compute/edit a per-candidate figure across 50 rows. Null means "use the per-candidate default
    /// as normal" (FeeConfiguration.RemitToVecAmount summed across every Paid payment) — the common
    /// case for teams whose real costs (e.g. Zoom) already justify keeping the full per-candidate
    /// amount. See GetFeeSummary.
    /// </summary>
    public decimal? RetainedAmountOverride { get; set; }
    public int? RetainedAmountOverrideByUserId { get; set; }
    public User? RetainedAmountOverrideByUser { get; set; }
    public DateTime? RetainedAmountOverrideUtc { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public DateTime? CancelledUtc { get; set; }

    /// <summary>Set when a reschedule is detected while the session already has candidates — a "something needs a human" flag, not an automatic action.</summary>
    public bool RescheduleFlaggedForReview { get; set; }
    public DateTime? RescheduleFlaggedUtc { get; set; }

    /// <summary>Set by the Session Manager's "mark session as completed" action; bulk-flips Candidate.Tested = true for every non-terminal candidate in the session.</summary>
    public DateTime? TestingCompletedUtc { get; set; }
    public int? TestingCompletedByUserId { get; set; }
    public User? TestingCompletedByUser { get; set; }

    /// <summary>
    /// Set by ingestion the first time ExamTools itself reports this session as closed ("done").
    /// **Deliberately separate from TestingCompletedUtc**, which means "a Session Manager clicked
    /// Mark completed" and carries real side effects (flipping candidates to Tested, completing
    /// Square orders, sending felony-disclosure emails). ExamTools closing a session is an
    /// observation, not that decision, so it must not silently trigger those.
    ///
    /// Its job is to record that there is nothing further to poll for this session, which makes it
    /// the guard against the false-cancellation bug (issue #68): the cancellation heuristic reads
    /// "a known, still-open session vanished from the feed", and before this field existed every
    /// real completed session eventually looked exactly like that once it aged out of the
    /// closed-session window — silently flipping genuine sessions to Cancelled 30 days after they ran.
    /// </summary>
    public DateTime? ExamToolsClosedUtc { get; set; }

    /// <summary>
    /// When this session's VE roster was last successfully fetched from ExamTools **while the
    /// session was already finished** — the "final poll" marker, and the thing that retires the
    /// session from `VolunteerExaminerSyncService`'s scan.
    ///
    /// <para>Null on an open session by design: a roster fetched mid-session says nothing about the
    /// final roster, since a VE can be added right up to (and just after) close. Once the session is
    /// finished, exactly one more successful fetch is needed, and this records that it happened. A
    /// fetch that throws never stamps it, so the retry is automatic — the usual scan-based idiom
    /// where one field is both the query filter and the idempotency guard.</para>
    /// </summary>
    public DateTime? VeRosterFinalSyncedUtc { get; set; }

    /// <summary>Renamed from Arrl* to Vec* (Phase 8) — submission goes to whichever VEC this session is actually under (VecId), not always ARRL specifically.</summary>
    public VecSubmissionStatus VecSubmissionStatus { get; set; } = VecSubmissionStatus.NotSubmitted;
    public DateTime? VecSubmittedDate { get; set; }
    public int? VecSubmittedByUserId { get; set; }
    public User? VecSubmittedByUser { get; set; }

    public DateTime CreatedUtc { get; set; }

    public List<Candidate> Candidates { get; } = [];
    public List<SessionVolunteerExaminer> SessionVolunteerExaminers { get; } = [];

    /// <summary>
    /// When this session finished, by whichever route got there first — a Session Manager marking it
    /// (<see cref="TestingCompletedUtc"/>) or ExamTools closing it (<see cref="ExamToolsClosedUtc"/>).
    /// Null means still open.
    ///
    /// <para><b>This is the definition of "completed", and it is deliberately not
    /// <see cref="Status"/>.</b> Status only ever leaves Active on *cancellation* — it is never set
    /// to Completed — so <c>Status == Active</c> means "not cancelled", and a query filtered on it
    /// returns every session the team has ever run. That misreading has shipped twice: it made
    /// VolunteerExaminerSyncService re-poll a team's entire history hourly for months, and then came
    /// back in the VE Roster's "sessions worked" count, where a VE rostered onto a *future* session
    /// already had it in their total.</para>
    ///
    /// <para><b>Not EF-mapped.</b> Use it on a materialized Session; query-side, spell the same rule
    /// out as <c>s.TestingCompletedUtc != null || s.ExamToolsClosedUtc != null</c> so EF can
    /// translate it. SessionCompletionRuleTests pins those two spellings together, since the
    /// language cannot.</para>
    /// </summary>
    public DateTime? CompletedUtc => TestingCompletedUtc ?? ExamToolsClosedUtc;

    /// <summary>Whether the session is finished — see <see cref="CompletedUtc"/> for the rule and
    /// for why this is not <c>Status</c>. Not EF-mapped.</summary>
    public bool IsCompleted => CompletedUtc is not null;

    /// <summary>True once the session's scheduled window has fully elapsed — used to keep
    /// backfilled/late-ingested past sessions (see SessionIngestionService's completed-session
    /// backfill) from triggering live Zoom/Discord scheduling or a "you're registered" email for
    /// something that already happened. Not EF-mapped, computed on demand — always call with the
    /// same TimeProvider-sourced `now` a service is already using, not DateTime.UtcNow directly.</summary>
    public bool HasEnded(DateTime now) => ScheduledStartUtc.AddMinutes(DurationMinutes) <= now;

    /// <summary>
    /// Session-level fee reconciliation — TotalCollected sums every Paid payment's Amount across
    /// every candidate in the session (only money actually in hand can be remitted). Without an
    /// override, TotalRemitToVec is the sum of each individual payment's own
    /// FeeConfiguration.RemitToVecAmount (the normal per-candidate default, clamped per-payment so a
    /// youth fee under the retained cap never goes negative). With RetainedAmountOverride set,
    /// TotalRemitToVec is instead TotalCollected minus that flat total, clamped at zero — no
    /// per-candidate math at all. TotalRetained is always whatever's left of TotalCollected. Requires
    /// FeeConfiguration and Candidates (with their Payments) loaded.
    /// </summary>
    public SessionFeeSummary GetFeeSummary()
    {
        var paidPayments = Candidates.SelectMany(c => c.Payments).Where(p => p.Status == PaymentStatus.Paid).ToList();
        var totalCollected = paidPayments.Sum(p => p.Amount);
        var totalRemitToVec = RetainedAmountOverride is { } overrideAmount
            ? Math.Max(0m, totalCollected - overrideAmount)
            : paidPayments.Sum(p => FeeConfiguration.RemitToVecAmount(p.Amount) ?? 0m);

        return new SessionFeeSummary(totalCollected, totalCollected - totalRemitToVec, totalRemitToVec);
    }
}

/// <summary>See Session.GetFeeSummary.</summary>
public record SessionFeeSummary(decimal TotalCollected, decimal TotalRetained, decimal TotalRemitToVec);
