using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;

namespace VeSessionManager.Core.Reconciliation;

/// <summary>
/// Asks ExamTools what it has, asks this database what it has, and records where they disagree
/// (built 2026-08-10).
///
/// <para><b>Why it exists.</b> Every other job here trusts ingestion to have worked. Nothing checked.
/// The historical import had been dropping the last day of every calendar month since it was
/// written — an exclusive end bound on ExamTools' closed-session feed — which cost roughly twelve
/// sessions a year, per team, silently: the requests succeeded, the responses were valid, and the
/// data simply was not there. It surfaced only because HRCC's own Discord bot reads the same API
/// directly and disagreed about whether a VE was still active. This job is that accident, done on
/// purpose.</para>
///
/// <para><b>It compares against the remote feed, not against itself.</b> That is the whole point and
/// also the limit: a bug shared by both sides stays invisible. Notably it would have caught the
/// date-bound bug regardless, because the sweep's window is wider than any single import chunk.</para>
///
/// <para><b>Read-only.</b> It records what it sees and never repairs anything. Fixing means
/// re-importing a range, which is a decision with API cost attached — the findings page offers the
/// button, a human presses it.</para>
/// </summary>
public class ReconciliationService(
    AppDbContext dbContext,
    IExamToolsClient examToolsClient,
    IOptions<ExamToolsOptions> examToolsOptions,
    TimeProvider timeProvider,
    ILogger<ReconciliationService> logger)
{
    /// <summary>
    /// How far back each sweep looks. Long enough that a gap has several chances to be noticed before
    /// it ages out, short enough to stay one cheap call per team — the whole session history would be
    /// a much larger ask of somebody else's API for a check that runs every night.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(120);

    public async Task<ReconciliationResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new ReconciliationResult();

        if (!team.IsExamToolsConfigured)
        {
            logger.LogInformation("Team {TeamId} ({TeamName}) has no ExamTools credentials — skipping reconciliation", team.Id, team.Name);
            return result;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var credentials = ExamToolsCredentials.For(team, examToolsOptions.Value.BaseUrl);

        var start = DateOnly.FromDateTime(now - Window);
        var end = DateOnly.FromDateTime(now);

        // Inclusive on both ends — see ExamToolsClient.ClosedSessionsPath for why that sentence needs
        // saying, and what it cost when it wasn't true.
        var remote = await examToolsClient.GetTeamClosedSessionsAsync(credentials, start, end, cancellationToken);
        result.RemoteSessions = remote.Count;

        // Anchored on the SAME boundary the remote feed was asked for (#280). This used to be
        // `now - Window`, which carries the run's time-of-day, while `start` above is midnight-
        // aligned — so the two windows disagreed by up to 24 hours at the far edge.
        //
        // The job's cadence is IntervalFromWorkerStart, so its run time-of-day is arbitrary. Run at
        // 14:00 UTC, a session at exactly day-120 02:00 UTC came back from the remote feed and was
        // excluded from `local` — a false MissingSession finding, on the standing table and in the
        // nav badge. It never resolved either: by the next night the session had aged out of both
        // windows, and RecordAsync only re-examines findings still inside the window.
        //
        // False alarms are particularly expensive here, on the one job whose entire purpose is to be
        // believed when it disagrees with the database.
        var windowStartUtc = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var local = await dbContext.Sessions
            .Where(s => s.TeamId == team.Id && s.ScheduledStartUtc >= windowStartUtc)
            .Select(s => new { s.Id, s.ExamToolsSessionId, CandidateCount = s.Candidates.Count })
            .ToListAsync(cancellationToken);

        var localById = local
            .ToDictionary(s => s.ExamToolsSessionId!, StringComparer.OrdinalIgnoreCase);
        result.LocalSessions = local.Count;

        var seen = new List<(ReconciliationFindingKind Kind, string SessionId, DateTime Date, string Detail)>();

        foreach (var session in remote)
        {
            if (!localById.TryGetValue(session.Id, out var localSession))
            {
                seen.Add((ReconciliationFindingKind.MissingSession, session.Id, session.Date,
                    $"ExamTools has a closed session on {session.Date:yyyy-MM-dd} that this app never ingested."));
                continue;
            }

            // Only ever flags "remote has MORE". Fewer is normal and not a fault: a candidate who
            // withdraws is removed at ExamTools while this app deliberately keeps the row, so
            // treating any difference as a discrepancy would fill the page with noise that is
            // working as designed.
            if (session.ApplicantCount is { } remoteCount && remoteCount > localSession.CandidateCount)
            {
                seen.Add((ReconciliationFindingKind.CandidateCountMismatch, session.Id, session.Date,
                    $"ExamTools reports {remoteCount} applicant(s) on the {session.Date:yyyy-MM-dd} session; this app has {localSession.CandidateCount}."));
            }
        }

        await RecordAsync(team.Id, seen, now, cancellationToken);

        result.MissingSessions = seen.Count(f => f.Kind == ReconciliationFindingKind.MissingSession);
        result.CandidateMismatches = seen.Count(f => f.Kind == ReconciliationFindingKind.CandidateCountMismatch);

        logger.LogInformation("Reconciliation for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// Folds this sweep's observations into the standing list: refresh what is still true, add what
    /// is new, resolve what has gone away.
    ///
    /// <para>Scoped to this team AND this window. A finding for a session that has simply aged out of
    /// the window must not be marked resolved — nothing was fixed, we just stopped looking, and
    /// silently clearing it would be the most misleading thing this job could do.</para>
    /// </summary>
    private async Task RecordAsync(
        int teamId,
        List<(ReconciliationFindingKind Kind, string SessionId, DateTime Date, string Detail)> seen,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var windowStart = now - Window;

        var existing = await dbContext.ReconciliationFindings
            .Where(f => f.TeamId == teamId && f.SessionDateUtc >= windowStart)
            .ToListAsync(cancellationToken);

        foreach (var (kind, sessionId, date, detail) in seen)
        {
            var match = existing.FirstOrDefault(f =>
                f.Kind == kind && string.Equals(f.ExamToolsSessionId, sessionId, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                dbContext.ReconciliationFindings.Add(new ReconciliationFinding
                {
                    TeamId = teamId,
                    Kind = kind,
                    ExamToolsSessionId = sessionId,
                    SessionDateUtc = date,
                    Detail = detail,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                });
                continue;
            }

            match.LastSeenUtc = now;
            match.Detail = detail;
            // A finding that came back after being resolved is unresolved again, not a second row.
            match.ResolvedUtc = null;
        }

        var stillOpen = seen
            .Select(f => (f.Kind, f.SessionId))
            .ToHashSet();

        foreach (var finding in existing.Where(f => f.ResolvedUtc is null))
        {
            if (!stillOpen.Contains((finding.Kind, finding.ExamToolsSessionId)))
            {
                finding.ResolvedUtc = now;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

public class ReconciliationResult
{
    public int RemoteSessions { get; set; }
    public int LocalSessions { get; set; }
    public int MissingSessions { get; set; }
    public int CandidateMismatches { get; set; }

    /// <summary>Becomes JobRunHistory.ResultSummary — so the ops dashboard says what was found, not merely that the job ran.</summary>
    public override string ToString() =>
        $"ExamTools {RemoteSessions} session(s), local {LocalSessions}; missing {MissingSessions}, candidate-count mismatches {CandidateMismatches}";
}
