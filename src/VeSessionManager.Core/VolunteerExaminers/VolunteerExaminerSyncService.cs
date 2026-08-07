using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Phase 7: syncs each team's active sessions' VE roster from ExamTools' export/full.json — the
/// only endpoint that returns a VE's display name, not just callsign (see docs/examtools-api.md
/// and docs/ve-tracking.md). Scan-based like every other phase: every poll, reconciles each active
/// session's SessionVolunteerExaminer links against whatever ExamTools currently reports for that
/// session, so a VE added or removed upstream is reflected automatically with no separate backfill
/// step. Cancelled sessions are left alone — their last-known roster is frozen, matching how
/// Zoom/Discord/payment state is also left as-is once a session is cancelled.
/// </summary>
public class VolunteerExaminerSyncService(
    AppDbContext dbContext,
    IExamToolsClient examToolsClient,
    IOptions<ExamToolsOptions> examToolsOptions,
    TimeProvider timeProvider,
    ILogger<VolunteerExaminerSyncService> logger)
{
    /// <summary>
    /// How long after a session's scheduled start this service keeps retrying a roster it has not
    /// managed to fetch. Beyond this a finished session is settled even with an empty roster — see
    /// the reasoning at the RemoveAll below. Anchored on ScheduledStartUtc, not ExamToolsClosedUtc,
    /// which the historical import stamps at *import* time (the same trap the payment and
    /// exam-result windows document).
    /// </summary>
    public static readonly TimeSpan RosterRetryWindow = TimeSpan.FromDays(30);


    /// <param name="onlySessionId">Restrict the sync to one session (the Detail page's
    /// session-scoped refresh); null (every scheduled/team-wide run) scans the whole team.</param>
    /// <param name="ignoreRetryWindow">
    /// Skips the <see cref="RosterRetryWindow"/> cutoff, so sessions older than it still get their
    /// roster fetched. Set by the historical import and nothing else — see the settle rule below for
    /// why the window and that feature are otherwise in direct conflict. Follows the same shape as
    /// ExamResultSyncService.SyncSessionAsync ignoring its own ResultSyncWindow.
    /// </param>
    public async Task<VeRosterSyncResult> RunAsync(Team team, CancellationToken cancellationToken, int? onlySessionId = null, bool ignoreRetryWindow = false)
    {
        var result = new VeRosterSyncResult();

        if (!team.IsExamToolsConfigured)
        {
            // Same skip-quietly convention as every other ExamTools-dependent step.
            logger.LogInformation("Team {TeamId} ({TeamName}) has no ExamTools credentials configured yet — skipping VE roster sync", team.Id, team.Name);
            return result;
        }

        var credentials = ExamToolsCredentials.For(team, examToolsOptions.Value.BaseUrl);

        // Preloaded and kept up to date in-memory for the rest of this run — a plain
        // FirstOrDefaultAsync-per-VE would miss a VE created earlier in the same run (not yet
        // saved), creating duplicate VolunteerExaminer rows for the same callsign.
        //
        // **Not scoped to this team any more (issue #142).** A VE is a person, and the person a
        // roster names may already exist because they serve another team — scoping the lookup would
        // recreate exactly the per-team duplication this change removed. The team-specific part is
        // the VeTeamMembership added below.
        var knownVes = await dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships)
            .Include(v => v.CallSignHistory)
            .Where(v => v.CallSign != null)
            .ToDictionaryAsync(v => v.CallSign!, cancellationToken);

        // Former call signs, so a roster still reporting someone's old call resolves to them rather
        // than minting a second person. Only consulted when the live call sign misses — see
        // ResolveVolunteerExaminer.
        var byFormerCallSign = await dbContext.VeCallSignHistories
            .GroupBy(h => h.CallSign)
            .Select(g => new { CallSign = g.Key, VeId = g.OrderByDescending(h => h.ReplacedUtc).First().VolunteerExaminerId })
            .ToDictionaryAsync(x => x.CallSign, x => x.VeId, cancellationToken);

        var sessions = await dbContext.Sessions
            .Include(s => s.SessionVolunteerExaminers).ThenInclude(sve => sve.VolunteerExaminer)
            .Where(s => s.TeamId == team.Id && s.Status == SessionStatus.Active
                        && (onlySessionId == null || s.Id == onlySessionId))
            .ToListAsync(cancellationToken);

        // Session.Status is NOT the "is this session over" signal, and reading it as one is why this
        // query grew without bound: Status stays Active forever unless a human clicks Mark
        // completed, so every session a team had ever ingested was re-polled, one API call each,
        // every tick, permanently. The UI has read ExamTools-closed sessions as "Completed" since
        // issue #71 — but that label is *derived* (TestingCompletedUtc ?? ExamToolsClosedUtc), which
        // makes this the one place a finished session still looked open. Tolerable while ingestion
        // reached ~30 days back; the historical import (issue #67) can add a year in one go.
        //
        // Three ways a session is done:
        //   ExamToolsClosedUtc  — ExamTools says the session is closed. The authoritative signal.
        //   TestingCompletedUtc — a Session Manager marked it completed.
        //   HasEnded            — the backstop for sessions that will never carry either stamp:
        //                         those ingested before ExamToolsClosedUtc existed, and any session
        //                         ExamTools drops without ever reporting "done".
        //
        // **Finished is not settled — one more successful fetch is (2026-08-07).** The rule used to
        // retire a session as soon as it was finished and had *any* VE stored, on the reasoning that
        // VEs are assigned before or during a session and never after. True of the exam, false of
        // the paperwork: a roster fetched mid-session is not the final roster, and this app polls
        // hourly, so anything ExamTools records between the last poll and the close was simply never
        // seen. That was invisible while session detail still offered a manual "+ Add VE"; removing
        // it (same day) made ExamTools the only route in.
        //
        // The fix is not a longer window but a marker: VeRosterFinalSyncedUtc, stamped only by a
        // successful fetch performed while the session was *already* finished. So a session is
        // polled exactly once more after it closes — the final update, capturing whatever the last
        // mid-session poll missed — and then never again. A fetch that throws leaves the stamp null,
        // so a failure retries by construction rather than being written off; that also covers the
        // session that appears and closes inside a single polling interval.
        //
        // **The window and the historical import are in direct conflict, and the import wins when it
        // asks (2026-08-07).** Every session a historical import creates is by definition older than
        // RosterRetryWindow, so this rule settled all of them on the very next line — the import's own
        // roster step was a guaranteed no-op for exactly the data it had just fetched, and it imported
        // sessions and candidates with no VEs at all. Reported live. `ignoreRetryWindow` is how the
        // import says "these are old on purpose, fetch anyway"; the routine path is unchanged.
        //
        // ...but "retry until it succeeds" is only right while the roster is still plausibly
        // *fetchable*. A real 2023 session pulled in by the historical import
        // (819 / 6567ff0cfb29450af7ba19da) returns HTTP 500 from ExamTools every time, so it never
        // stamped, never settled, and produced one failed API call plus one ERROR line every hour,
        // forever. Past RosterRetryWindow a finished session is settled whether or not its final
        // fetch ever succeeded: nobody is assigning VEs to a session from two years ago, so a roster
        // that can't be fetched by then is a fact about ExamTools, not a sync still worth retrying.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var retryCutoff = now - RosterRetryWindow;
        var settled = sessions.RemoveAll(s =>
            IsFinished(s, now)
            && (s.VeRosterFinalSyncedUtc is not null || (!ignoreRetryWindow && s.ScheduledStartUtc < retryCutoff)));
        if (settled > 0)
        {
            logger.LogInformation("VE roster sync for team {TeamId} ({TeamName}): skipped {SettledCount} finished session(s) already synced after close, or started over {RetryWindowDays} days ago", team.Id, team.Name, settled, RosterRetryWindow.TotalDays);
        }

        // Each session isolated and saved independently — same reasoning as every other scan-based
        // service's per-item try/catch + save: one session's ExamTools call throwing must not skip
        // every later session in this team's list, nor discard reconciliation already done for
        // earlier ones by leaving it all pending on a single end-of-loop SaveChangesAsync.
        foreach (var session in sessions)
        {
            try
            {
                var roster = await examToolsClient.GetSessionVeRosterAsync(credentials, session.ExamToolsSessionId, cancellationToken);
                ReconcileSession(team, session, roster, knownVes, byFormerCallSign, now, result);

                // Stamped here, after the call returned, and only for a session that was already
                // finished when we asked — that combination is the whole meaning of the field. A
                // successful fetch on an *open* session must not stamp it: its roster can still
                // change, and the final poll would then never happen.
                if (IsFinished(session, now))
                {
                    session.VeRosterFinalSyncedUtc = now;
                    result.SessionsFinalised++;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sync VE roster for session {SessionId} ({ExamToolsSessionId})", session.Id, session.ExamToolsSessionId);
            }
        }

        logger.LogInformation("VE roster sync finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// The one definition of "this session is over", used by both the settle rule and the stamp so
    /// they cannot disagree about which sessions get their final poll. Note it is not
    /// <c>Status</c> — that only ever means "not cancelled" (see CLAUDE.md).
    /// </summary>
    private static bool IsFinished(Session session, DateTime now) =>
        session.ExamToolsClosedUtc is not null || session.TestingCompletedUtc is not null || session.HasEnded(now);

    private void ReconcileSession(
        Team team, Session session, IReadOnlyList<ExamToolsVe> roster,
        Dictionary<string, VolunteerExaminer> knownVes, Dictionary<string, int> byFormerCallSign,
        DateTime now, VeRosterSyncResult result)
    {
        var rosterCallSigns = roster
            .Where(v => !string.IsNullOrWhiteSpace(v.Call))
            .Select(v => v.Call.Trim().ToUpperInvariant())
            .ToHashSet();

        foreach (var link in session.SessionVolunteerExaminers
                     .Where(l => !rosterCallSigns.Contains(l.VolunteerExaminer.CallSign ?? ""))
                     .ToList())
        {
            session.SessionVolunteerExaminers.Remove(link);
            dbContext.SessionVolunteerExaminers.Remove(link);
            result.LinksRemoved++;
        }

        var existingCallSigns = session.SessionVolunteerExaminers
            .Select(l => l.VolunteerExaminer.CallSign ?? "")
            .ToHashSet();

        foreach (var ve in roster)
        {
            if (string.IsNullOrWhiteSpace(ve.Call))
            {
                continue;
            }

            var callSign = ve.Call.Trim().ToUpperInvariant();
            var name = ve.Name.Trim();

            if (!knownVes.TryGetValue(callSign, out var volunteerExaminer))
            {
                // Second chance before creating a person: the roster may still be naming someone by
                // a call sign they no longer hold. Creating a duplicate here would split their
                // session history in two and hand them a second, empty set of contact details.
                if (byFormerCallSign.TryGetValue(callSign, out var formerId))
                {
                    volunteerExaminer = knownVes.Values.FirstOrDefault(v => v.Id == formerId);
                }

                if (volunteerExaminer is null)
                {
                    volunteerExaminer = new VolunteerExaminer
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? callSign : name,
                        CallSign = callSign,
                        CreatedUtc = now
                    };
                    dbContext.VolunteerExaminers.Add(volunteerExaminer);
                    result.VolunteerExaminersAdded++;
                }

                knownVes[callSign] = volunteerExaminer;
            }

            // **Name is NOT refreshed from ExamTools.** It used to be, on the stated grounds that
            // nothing in the app could edit it — true then, false since issue #142 gave admins and
            // the VEs themselves an edit screen. Re-applying the feed's value every poll would
            // silently undo those edits within the hour, which is the same trap the manual VE roster
            // buttons fell into. ExamTools seeds the name once, at creation above, and owns nothing
            // about this person afterwards; team membership below is the only thing it still drives.

            EnsureTeamMembership(volunteerExaminer, team, now, result);

            if (!existingCallSigns.Contains(callSign))
            {
                session.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer
                {
                    Session = session,
                    VolunteerExaminer = volunteerExaminer
                });
                existingCallSigns.Add(callSign);
                result.LinksAdded++;
            }
        }
    }

    /// <summary>
    /// Working a session for a team is what makes someone a member of it, so the membership is
    /// created here on first sight.
    ///
    /// <para><b>It is never removed, and <see cref="VeTeamMembership.IsActive"/> is never touched.</b>
    /// Retiring a VE from a team is a human decision an admin makes; if this method "corrected" it,
    /// an inactivated VE who then turned up on one more session roster would quietly reactivate
    /// themselves. ExamTools owns whether a membership exists, an admin owns whether it is
    /// active.</para>
    /// </summary>
    private void EnsureTeamMembership(VolunteerExaminer volunteerExaminer, Team team, DateTime now, VeRosterSyncResult result)
    {
        if (volunteerExaminer.TeamMemberships.Any(m => m.TeamId == team.Id))
        {
            return;
        }

        var membership = new VeTeamMembership
        {
            VolunteerExaminer = volunteerExaminer,
            TeamId = team.Id,
            IsActive = true,
            CreatedUtc = now
        };
        volunteerExaminer.TeamMemberships.Add(membership);
        dbContext.VeTeamMemberships.Add(membership);
        result.TeamMembershipsAdded++;
    }
}

public class VeRosterSyncResult
{
    public int VolunteerExaminersAdded { get; set; }

    /// <summary>Kept at zero since issue #142 — ExamTools no longer updates anything on an existing VE. Retained so the summary line's shape (and Job History's stored text) doesn't change under anyone reading old runs.</summary>
    public int VolunteerExaminersUpdated { get; set; }

    /// <summary>New (VE, team) memberships established by a VE turning up on that team's roster.</summary>
    public int TeamMembershipsAdded { get; set; }
    public int LinksAdded { get; set; }
    public int LinksRemoved { get; set; }

    /// <summary>Sessions that took their final post-close roster sync this run and will not be polled again.</summary>
    public int SessionsFinalised { get; set; }

    public override string ToString() =>
        $"VEs added {VolunteerExaminersAdded}, team memberships added {TeamMembershipsAdded}, links added {LinksAdded}, links removed {LinksRemoved}, sessions finalised {SessionsFinalised}";
}
