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
    public async Task<VeRosterSyncResult> RunAsync(Team team, CancellationToken cancellationToken, int? onlySessionId = null)
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
        var knownVes = await dbContext.VolunteerExaminers
            .Where(v => v.TeamId == team.Id && v.CallSign != null)
            .ToDictionaryAsync(v => v.CallSign!, cancellationToken);

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
        // Three ways a session is done, and the roster check is what makes skipping safe:
        //   ExamToolsClosedUtc  — ExamTools says the session is closed. The authoritative signal.
        //   TestingCompletedUtc — a Session Manager marked it completed.
        //   HasEnded            — the backstop for sessions that will never carry either stamp:
        //                         those ingested before ExamToolsClosedUtc existed, and any session
        //                         ExamTools drops without ever reporting "done".
        //
        // The roster check is not redundant. VEs are assigned before or during a session, never
        // after, so a finished session *with* VEs recorded really is finished — but a session that
        // appears and closes inside a single polling interval would otherwise be skipped before its
        // roster was ever fetched, losing it permanently. An empty roster keeps being retried, so a
        // sync that failed at the time self-heals instead of being silently written off.
        //
        // ...but "retry forever" is only right while the roster is still plausibly *fetchable*. A
        // real 2023 session pulled in by the historical import (819 / 6567ff0cfb29450af7ba19da)
        // returns HTTP 500 from ExamTools every time, so its roster stayed empty, it never settled,
        // and it produced one failed API call plus one ERROR line every hour, forever. Past
        // RosterRetryWindow a finished session is settled whether or not a roster was ever obtained:
        // nobody is assigning VEs to a session from two years ago, so an empty roster that old is a
        // fact about ExamTools, not a sync still worth retrying.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var retryCutoff = now - RosterRetryWindow;
        var settled = sessions.RemoveAll(s =>
            (s.ExamToolsClosedUtc is not null || s.TestingCompletedUtc is not null || s.HasEnded(now))
            && (s.SessionVolunteerExaminers.Count > 0 || s.ScheduledStartUtc < retryCutoff));
        if (settled > 0)
        {
            logger.LogInformation("VE roster sync for team {TeamId} ({TeamName}): skipped {SettledCount} finished session(s) that already have a roster or started over {RetryWindowDays} days ago", team.Id, team.Name, settled, RosterRetryWindow.TotalDays);
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
                ReconcileSession(team, session, roster, knownVes, result);
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

    private void ReconcileSession(
        Team team, Session session, IReadOnlyList<ExamToolsVe> roster,
        Dictionary<string, VolunteerExaminer> knownVes, VeRosterSyncResult result)
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
                volunteerExaminer = new VolunteerExaminer
                {
                    Name = string.IsNullOrWhiteSpace(name) ? callSign : name,
                    CallSign = callSign,
                    TeamId = team.Id
                };
                dbContext.VolunteerExaminers.Add(volunteerExaminer);
                knownVes[callSign] = volunteerExaminer;
                result.VolunteerExaminersAdded++;
            }
            else if (!string.IsNullOrWhiteSpace(name) && volunteerExaminer.Name != name)
            {
                // No manual-edit path exists yet (Phase 9), so ExamTools stays the single source of
                // truth for Name — unlike CallSign-matched Frn on Candidate, there's nothing to
                // preserve against yet.
                volunteerExaminer.Name = name;
                result.VolunteerExaminersUpdated++;
            }

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
}

public class VeRosterSyncResult
{
    public int VolunteerExaminersAdded { get; set; }
    public int VolunteerExaminersUpdated { get; set; }
    public int LinksAdded { get; set; }
    public int LinksRemoved { get; set; }

    public override string ToString() =>
        $"VEs added {VolunteerExaminersAdded}, VEs updated {VolunteerExaminersUpdated}, links added {LinksAdded}, links removed {LinksRemoved}";
}
