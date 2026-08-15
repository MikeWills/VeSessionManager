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
        //
        // Loads every VE, including ones with no call sign at all — the importer can create a
        // name-only row, and the former-call-sign fallback has to be able to resolve to one (issue
        // #282). The call-sign-keyed maps below still filter those out; only the by-id map needs them.
        var allVes = await dbContext.VolunteerExaminers
            .Include(v => v.TeamMemberships)
            .Include(v => v.CallSignHistory)
            .ToListAsync(cancellationToken);

        var withCallSign = allVes.Where(v => v.CallSign != null).ToList();

        // Keyed on the call sign, so only rows whose call sign is actually an identity may go in.
        // Placeholders are excluded for two reasons: matching on one fuses different people (see
        // ReconcileSession), and — because several people can legitimately hold the literal
        // "<UNKNOWN>" — a straight ToDictionary over every row throws a duplicate-key exception the
        // moment more than one exists, taking the whole roster sync down with it.
        //
        // **GroupBy first (issue #278).** CallSign is explicitly NOT unique — the migration was
        // designed to leave genuine duplicates for a human to merge, and
        // VolunteerExaminerDirectoryService queries for exactly that to render its "possible
        // duplicate" marker. So a usable call sign can legitimately appear twice, and the bare
        // ToDictionary that used to be here would throw on it — outside the per-session try/catch,
        // so it took the whole team's roster sync down every tick until someone merged them. The
        // placeholder map two lines below already did this correctly; only this one did not.
        var knownVes = withCallSign
            .Where(v => CallSign.IsUsable(v.CallSign))
            .GroupBy(v => v.CallSign!)
            .ToDictionary(g => g.Key, g => g.First());

        // Unidentifiable rows are matched within one team only, which is what the old per-team
        // schema did implicitly and what stops a new person being minted on every poll.
        var placeholderVesOnThisTeam = withCallSign
            .Where(v => !CallSign.IsUsable(v.CallSign) && v.TeamMemberships.Any(m => m.TeamId == team.Id))
            .GroupBy(v => v.CallSign!)
            .ToDictionary(g => g.Key, g => g.First());

        // Resolves a former-call-sign hit to the person, whatever their current call sign is —
        // including null or a placeholder (issue #282). This used to search knownVes, a strict
        // subset, so a VE whose current call sign was unusable fell through to "create", minting a
        // second person and splitting their session history: precisely the outcome the
        // former-call-sign lookup exists to prevent.
        var allById = allVes.ToDictionary(v => v.Id);

        // Former call signs, so a roster still reporting someone's old call resolves to them rather
        // than minting a second person. Only consulted when the live call sign misses — see
        // ResolveVolunteerExaminer.
        var byFormerCallSign = await dbContext.VeCallSignHistories
            .GroupBy(h => h.CallSign)
            .Select(g => new { CallSign = g.Key, VeId = g.OrderByDescending(h => h.ReplacedUtc).First().VolunteerExaminerId })
            .ToDictionaryAsync(x => x.CallSign, x => x.VeId, cancellationToken);

        // **VeRosterFinalSyncedUtc == null is what bounds this query (issue #245).** Status ==
        // Active means "not cancelled" and nothing else, so on its own this loaded every session the
        // team has ever run — with its roster links and each linked VE — on every tick, only to
        // discard nearly all of them in the RemoveAll a few lines below. The HTTP calls were bounded
        // by that in-memory filter, which is why the symptom looked fixed; the query never was.
        //
        // Safe because the stamp is only ever written for a session that was already finished, and
        // finished is monotonic — so "has a stamp" implies "is settled", which is exactly the first
        // arm of the settle rule below. The second arm (the retry window) still has to be applied in
        // memory, because HasEnded is C# arithmetic and cannot translate. The historical import's
        // ignoreRetryWindow path is unaffected: those rows have no stamp.
        var sessions = await dbContext.Sessions
            .Include(s => s.SessionVolunteerExaminers).ThenInclude(sve => sve.VolunteerExaminer)
            .Where(s => s.TeamId == team.Id && s.Status == SessionStatus.Active
                        && s.VeRosterFinalSyncedUtc == null
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
        // The stamped arm is now handled by the query above; what is left is the retry window, which
        // depends on HasEnded and so cannot translate to SQL.
        var settled = sessions.RemoveAll(s =>
            IsFinished(s, now) && !ignoreRetryWindow && s.ScheduledStartUtc < retryCutoff);
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
                ReconcileSession(team, session, roster, knownVes, placeholderVesOnThisTeam, byFormerCallSign, allById, now, result);

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
                // **Discard this session's pending link changes (issue #233).** Without this the
                // failed session's Add/Remove entries stayed tracked, so every later session's
                // SaveChangesAsync re-attempted them and threw the same error — one bad session
                // failing all of them, with the log showing N identical failures and no hint that
                // only the first was real.
                //
                // Scoped to the link rows on purpose, and NOT ChangeTracker.Clear(): that would also
                // detach any VolunteerExaminer created earlier in this run, which the knownVes cache
                // still holds references to — later sessions would then reuse a detached person and
                // silently mint duplicates. New people and memberships are genuinely on the roster
                // and are left pending for the next session's save to commit.
                DetachSessionLinks(session);
                logger.LogError(ex, "Failed to sync VE roster for session {SessionId} ({ExamToolsSessionId})", session.Id, session.ExamToolsSessionId);
            }
        }

        logger.LogInformation("VE roster sync finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// Detaches the roster-link rows one failed session left pending, and nothing else — see the
    /// catch block that calls it (issue #233).
    /// </summary>
    private void DetachSessionLinks(Session session)
    {
        var pending = dbContext.ChangeTracker.Entries<SessionVolunteerExaminer>()
            .Where(e => e.State != EntityState.Unchanged
                        && (e.Entity.SessionId == session.Id || ReferenceEquals(e.Entity.Session, session)))
            .ToList();

        foreach (var entry in pending)
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// The one definition of "this session is over", used by both the settle rule and the stamp so
    /// they cannot disagree about which sessions get their final poll. Note it is not
    /// <c>Status</c> — that only ever means "not cancelled" (see CLAUDE.md).
    /// </summary>
    private static bool IsFinished(Session session, DateTime now) =>
        session.IsCompleted || session.HasEnded(now);

    private void ReconcileSession(
        Team team, Session session, IReadOnlyList<ExamToolsVe> roster,
        Dictionary<string, VolunteerExaminer> knownVes,
        Dictionary<string, VolunteerExaminer> placeholderVesOnThisTeam,
        Dictionary<string, int> byFormerCallSign,
        Dictionary<int, VolunteerExaminer> allById,
        DateTime now, VeRosterSyncResult result)
    {
        // **Resolve first, then diff — and diff on the person, not the call sign (issue #283).**
        //
        // The link table's key is (SessionId, VolunteerExaminerId), so a call-sign-keyed diff was
        // answering a different question from the one the database asks, and got two things wrong:
        //
        //   Duplicate primary key. A roster naming the same person twice — once by their current
        //   call sign, once by a former one, which byFormerCallSign resolves to the same row — passed
        //   the call-sign guard twice and added two links with an identical composite key. That
        //   throws on save, and until #233 below the throw also poisoned every later session in the
        //   team's run.
        //
        //   Perpetual churn. After the license sweep follows FCC onto a new call sign, a roster
        //   still reporting the old one no longer matches the stored VE's CallSign, so the link was
        //   dropped and re-added on every single tick — LinksRemoved and LinksAdded both ticking up
        //   forever for a roster that had not changed.
        var rosterVes = ResolveRoster(team, roster, knownVes, placeholderVesOnThisTeam, byFormerCallSign, allById, now, result);
        var rosterVeIds = rosterVes.Select(v => v.Id).Where(id => id != 0).ToHashSet();

        foreach (var link in session.SessionVolunteerExaminers
                     .Where(l => !rosterVeIds.Contains(l.VolunteerExaminerId))
                     .ToList())
        {
            session.SessionVolunteerExaminers.Remove(link);
            dbContext.SessionVolunteerExaminers.Remove(link);
            result.LinksRemoved++;
        }

        // Tracked by identity, not by call sign — a person newly created in this same pass has Id 0
        // until save, so reference equality is what distinguishes them.
        var linked = session.SessionVolunteerExaminers
            .Select(l => l.VolunteerExaminer)
            .ToHashSet();

        foreach (var volunteerExaminer in rosterVes)
        {
            EnsureTeamMembership(volunteerExaminer, team, now, result);

            if (linked.Add(volunteerExaminer))
            {
                session.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer
                {
                    Session = session,
                    VolunteerExaminer = volunteerExaminer
                });
                result.LinksAdded++;
            }
        }
    }

    /// <summary>
    /// Turns one ExamTools roster into the set of people it names, creating anyone genuinely new.
    /// Split out of <see cref="ReconcileSession"/> (issue #283) so the link diff can work in terms of
    /// people rather than call signs — the same terms the link table's own key uses.
    ///
    /// <para>Returns distinct people: a roster naming someone twice, under a current and a former
    /// call sign, resolves to one person and must produce one link.</para>
    /// </summary>
    private List<VolunteerExaminer> ResolveRoster(
        Team team, IReadOnlyList<ExamToolsVe> roster,
        Dictionary<string, VolunteerExaminer> knownVes,
        Dictionary<string, VolunteerExaminer> placeholderVesOnThisTeam,
        Dictionary<string, int> byFormerCallSign,
        Dictionary<int, VolunteerExaminer> allById,
        DateTime now, VeRosterSyncResult result)
    {
        var resolved = new List<VolunteerExaminer>();
        var seen = new HashSet<VolunteerExaminer>();

        foreach (var ve in roster)
        {
            if (string.IsNullOrWhiteSpace(ve.Call))
            {
                continue;
            }

            var callSign = CallSign.NormalizeFormat(ve.Call)!;
            var name = ve.Name.Trim();

            // **A placeholder is not an identity.** ExamTools reports the literal "<UNKNOWN>" when it
            // has no call sign, and treated as an ordinary value it looks like one call sign shared
            // by many different people. Matching on it fused HRCC's unidentified VE with MARC's into
            // a single person carrying 88 sessions of both their histories (found live 2026-08-07).
            //
            // Such a row is still matched *within one team*, which is what the old per-team schema
            // did implicitly and what stops a new person being created on every single poll — but it
            // can never match across teams, so two teams' unknowns stay two people.
            var identifiable = CallSign.IsUsable(callSign);

            // Lookup: a real call sign identifies a person anywhere; a placeholder only within
            // this team.
            VolunteerExaminer? volunteerExaminer = null;
            if (identifiable)
            {
                knownVes.TryGetValue(callSign, out volunteerExaminer);
            }
            else
            {
                placeholderVesOnThisTeam.TryGetValue(callSign, out volunteerExaminer);
            }

            // Second chance before creating a person: the roster may still be naming someone by a
            // call sign they no longer hold. Creating a duplicate here would split their session
            // history in two and hand them a second, empty set of contact details. Real call signs
            // only — a placeholder has no history worth following.
            if (volunteerExaminer is null && identifiable && byFormerCallSign.TryGetValue(callSign, out var formerId))
            {
                // allById, not knownVes (issue #282) — knownVes holds only people whose *current*
                // call sign is usable, so someone who has since become a placeholder, or lost their
                // call sign entirely, was invisible here and got a second record minted.
                allById.TryGetValue(formerId, out volunteerExaminer);
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
                allById[volunteerExaminer.Id] = volunteerExaminer;
                result.VolunteerExaminersAdded++;
            }

            // Cached so a VE created earlier in this same run is found again rather than duplicated.
            // A usable call sign goes in the global map; a placeholder only in this team's, so the
            // next team's sync cannot pick up the person we just created for this one.
            if (identifiable)
            {
                knownVes[callSign] = volunteerExaminer;
            }
            else
            {
                placeholderVesOnThisTeam[callSign] = volunteerExaminer;
            }

            // **Name is NOT refreshed from ExamTools.** It used to be, on the stated grounds that
            // nothing in the app could edit it — true then, false since issue #142 gave admins and
            // the VEs themselves an edit screen. Re-applying the feed's value every poll would
            // silently undo those edits within the hour, which is the same trap the manual VE roster
            // buttons fell into. ExamTools seeds the name once, at creation above, and owns nothing
            // about this person afterwards; team membership, applied by the caller, is the only
            // thing it still drives.

            // Distinct by person: two roster entries naming the same human — current call sign and a
            // former one — must yield one link, not a duplicate primary key.
            if (seen.Add(volunteerExaminer))
            {
                resolved.Add(volunteerExaminer);
            }
        }

        return resolved;
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

    /// <summary>New (VE, team) memberships established by a VE turning up on that team's roster.</summary>
    public int TeamMembershipsAdded { get; set; }
    public int LinksAdded { get; set; }
    public int LinksRemoved { get; set; }

    /// <summary>Sessions that took their final post-close roster sync this run and will not be polled again.</summary>
    public int SessionsFinalised { get; set; }

    public override string ToString() =>
        $"VEs added {VolunteerExaminersAdded}, team memberships added {TeamMembershipsAdded}, links added {LinksAdded}, links removed {LinksRemoved}, sessions finalised {SessionsFinalised}";
}
