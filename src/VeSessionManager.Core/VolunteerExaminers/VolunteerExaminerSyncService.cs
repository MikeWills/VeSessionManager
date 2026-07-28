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
    ILogger<VolunteerExaminerSyncService> logger)
{
    public async Task<VeRosterSyncResult> RunAsync(Team team, CancellationToken cancellationToken)
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
            .Where(s => s.TeamId == team.Id && s.Status == SessionStatus.Active)
            .ToListAsync(cancellationToken);

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
