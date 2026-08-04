using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;

namespace VeSessionManager.Core.ExamResults;

/// <summary>
/// Auto-detects a candidate's graded exam result straight from ExamTools instead of waiting for a
/// Session Manager to click "Mark failed"/"Mark session as completed" by hand — found live 2026-07-28
/// when a real HRCC candidate's failed exam ("Terrance A Harris") hadn't been reflected in the app at
/// all, even though ExamTools' own per-applicant detail endpoint
/// (GET .../sessions/{sessionId}/applicant/{applicantId}) already had the graded result the whole
/// time (an endpoint ingestion had never called before — see docs/examtools-api.md's "Applicant exam
/// results" section for the real payload shape).
///
/// Scan-based like every other phase: every poll, for each Active session whose start has already
/// passed, checks every non-terminal, not-yet-Tested candidate's exams[]. A candidate with any
/// graded-and-failed element is flipped straight to ApplicationStatus=Failed (same fields
/// CandidateActionService.MarkFailedAsync sets, but ResultMarkedByUserId stays null — nobody manually
/// clicked anything, so there's no user to attribute it to) — this also makes PaymentReminderService's
/// existing Reason=Retest reminder logic (which is gated on ResultMarkedUtc) fire automatically for
/// these candidates for the first time, closing a second latent gap along with the first. A candidate
/// whose graded exam(s) all passed just gets Tested=true, leaving ApplicationStatus alone exactly like
/// the manual "mark session completed" bulk-flip does (a pass still waits on the FCC watcher for the
/// eventual Granted transition).
///
/// Once Tested is true (either from here or the manual bulk-flip) or ApplicationStatus is terminal, a
/// candidate is never checked again — bounds this to a handful of API calls per tick, not the whole
/// candidate history, and avoids repeatedly pulling this endpoint's fuller PII payload for rows that
/// don't need it anymore. The one exception is NewLicenseClass (below): Failed/NotTested candidates
/// are still excluded forever (they never earn a class), but an already-Tested, non-Failed candidate
/// missing NewLicenseClass is still re-scanned — this is what backfills every pre-existing "current,
/// past, and future" candidate (issue reported 2026-07-29) the first time this ships, using the same
/// scan-based idempotent-field pattern as everything else, rather than a one-off migration script.
///
/// License class (2026-07-29): a candidate's initial license class (walking in) and new license class
/// (walking out) are derived purely from which exam elements ExamTools reports as graded+passed this
/// sitting — not from FCC ULS/AM.dat's operator-class field, which this app has never fetched (see
/// Candidate.LicenseGrantPredatesSession's remarks on why AM.dat parsing was deliberately avoided
/// there too). This works because a VE session never re-administers an element a candidate already
/// holds credit for: the lowest element passed this sitting implies every element below it (down to
/// Technician's Element 2) was already held coming in, and the highest element passed this sitting is
/// the new class. E.g. passing only Element 4 this sitting implies walking in with General (Element
/// 2+3 credit) and walking out with Extra. See ResolveLicenseClasses.
/// </summary>
public class ExamResultSyncService(
    AppDbContext dbContext,
    IExamToolsClient examToolsClient,
    TimeProvider timeProvider,
    IOptions<ExamToolsOptions> examToolsOptions,
    ILogger<ExamResultSyncService> logger)
{
    /// <summary>
    /// How long after a session's scheduled start its candidates keep being checked for graded
    /// results (issue #81). Generous — results are normally entered the same day or the next — but
    /// finite, which is the point: without a bound this scan grows forever. A session graded later
    /// than this can still be pulled in on demand, because ManualCandidateRefreshService runs this
    /// service and the "Refresh now" button on Admin → Team Maintenance runs that.
    /// </summary>
    public static readonly TimeSpan ResultSyncWindow = TimeSpan.FromDays(14);

    public async Task<ExamResultSyncResult> RunAsync(Team team, CancellationToken cancellationToken)
    {
        var result = new ExamResultSyncResult();

        if (!team.IsExamToolsConfigured)
        {
            logger.LogInformation("Team {TeamId} ({TeamName}) has no ExamTools credentials configured yet — skipping exam result sync", team.Id, team.Name);
            return result;
        }

        var credentials = ExamToolsCredentials.For(team, examToolsOptions.Value.BaseUrl);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Issue #81. `Status` only ever leaves Active on *cancellation* — it is never set to
        // Completed (see CLAUDE.md's Known Constraints) — so "Active and already started" means
        // every session this team has ever run, with no upper bound. The per-candidate gate below
        // keeps a fully-resolved session free, but any candidate that never resolves (a no-show
        // whose ExamTools record carries no result data is the common case) was polled once per
        // tick, forever. The historical import made that materially worse: imported candidates
        // arrive Tested=false, so a year of history is a burst of one call each, plus a permanent
        // residue for every one that never resolves.
        //
        // Bounded by how long ago the session RAN. Exam results are entered during or shortly after
        // a session; one that ran months ago will not start producing new results because we asked
        // again. Anchored on ScheduledStartUtc and not on ExamToolsClosedUtc deliberately — the
        // historical import stamps the close field at *import* time, so anchoring there would keep
        // freshly-imported March sessions eligible for the full window and preserve the burst this
        // exists to stop.
        var cutoff = now - ResultSyncWindow;
        var sessions = await dbContext.Sessions
            .Include(s => s.Candidates)
            .Where(s => s.TeamId == team.Id && s.Status == SessionStatus.Active
                        && s.ScheduledStartUtc <= now && s.ScheduledStartUtc >= cutoff)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            await SyncSessionCandidatesAsync(credentials, session, now, result, cancellationToken);
        }

        logger.LogInformation("Exam result sync finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
    }

    /// <summary>
    /// On-demand single-session variant of RunAsync with NO ResultSyncWindow bound — this is the
    /// escape hatch ResultSyncWindow's own doc comment promises for a session graded later than the
    /// window. (Until 2026-08-03 the manual refresh ran RunAsync, whose window applied regardless,
    /// so the promised escape hatch didn't actually exist.) Still requires the session to be
    /// non-cancelled and already started: a cancelled session has no results to sync, and a future
    /// one can't have any yet.
    /// </summary>
    public async Task<ExamResultSyncResult> SyncSessionAsync(Team team, int sessionId, CancellationToken cancellationToken)
    {
        var result = new ExamResultSyncResult();

        if (!team.IsExamToolsConfigured)
        {
            logger.LogInformation("Team {TeamId} ({TeamName}) has no ExamTools credentials configured yet — skipping exam result sync", team.Id, team.Name);
            return result;
        }

        var credentials = ExamToolsCredentials.For(team, examToolsOptions.Value.BaseUrl);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var session = await dbContext.Sessions
            .Include(s => s.Candidates)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TeamId == team.Id
                        && s.Status == SessionStatus.Active && s.ScheduledStartUtc <= now,
                cancellationToken);
        if (session is not null)
        {
            await SyncSessionCandidatesAsync(credentials, session, now, result, cancellationToken);
        }

        logger.LogInformation("Exam result sync finished for session {SessionId}, team {TeamId} ({TeamName}): {Result}", sessionId, team.Id, team.Name, result);
        return result;
    }

    private async Task SyncSessionCandidatesAsync(ExamToolsCredentials credentials, Session session, DateTime now, ExamResultSyncResult result, CancellationToken cancellationToken)
    {
        var pendingCandidates = session.Candidates
            .Where(c => c.ExamToolsApplicantId is not null
                && c.ApplicationStatus != CandidateApplicationStatus.Failed
                && c.ApplicationStatus != CandidateApplicationStatus.NotTested
                && (!c.Tested || c.NewLicenseClass is null))
            .ToList();

        foreach (var candidate in pendingCandidates)
        {
            try
            {
                var detail = await examToolsClient.GetApplicantDetailAsync(credentials, session.ExamToolsSessionId, candidate.ExamToolsApplicantId!, cancellationToken);
                ApplyResult(candidate, detail, now, result);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sync exam result for candidate {CandidateId} in session {SessionId} ({ExamToolsSessionId})", candidate.Id, session.Id, session.ExamToolsSessionId);
            }
        }
    }

    private void ApplyResult(Candidate candidate, ExamToolsApplicantDetail? detail, DateTime now, ExamResultSyncResult result)
    {
        var gradedExams = detail?.Exams.Where(e => e.Graded).ToList() ?? [];
        if (gradedExams.Count == 0)
        {
            // Not graded yet (or the applicant detail call came back empty) — leave alone, next poll retries.
            return;
        }

        if (gradedExams.Any(e => !e.Passed))
        {
            candidate.ApplicationStatus = CandidateApplicationStatus.Failed;
            candidate.Tested = true;
            candidate.ResultMarkedUtc = now;
            candidate.ResultMarkedByUserId = null;

            dbContext.AddAuditLog(null, "CandidateAutoMarkedFailed", nameof(Candidate), candidate.Id,
                $"Candidate {candidate.Id} auto-marked Failed from ExamTools' graded exam result.", now);
            result.CandidatesMarkedFailed++;
        }
        else
        {
            var wasAlreadyTested = candidate.Tested;
            candidate.Tested = true;

            if (candidate.NewLicenseClass is null)
            {
                var (initial, newClass) = ResolveLicenseClasses(gradedExams.Select(e => e.Element));
                candidate.InitialLicenseClass = initial;
                candidate.NewLicenseClass = newClass;
            }

            if (wasAlreadyTested)
            {
                result.CandidatesBackfilledLicenseClass++;
            }
            else
            {
                result.CandidatesMarkedTested++;
            }
        }
    }

    /// <summary>
    /// Infers the class held walking in and the class earned walking out from the set of elements
    /// graded+passed this sitting — see class remarks for why no FCC data is needed. Element 2 =
    /// Technician, 3 = General, 4 = Extra (Element 1/Morse code retired 2007).
    /// </summary>
    internal static (LicenseClass Initial, LicenseClass New) ResolveLicenseClasses(IEnumerable<int> passedElements)
    {
        var elements = passedElements.ToList();
        var lowestPassed = elements.Min();
        var highestPassed = elements.Max();
        return (ClassForElement(lowestPassed - 1), ClassForElement(highestPassed));
    }

    private static LicenseClass ClassForElement(int element) => element switch
    {
        2 => LicenseClass.Technician,
        3 => LicenseClass.General,
        4 => LicenseClass.Extra,
        _ => LicenseClass.None
    };
}

public class ExamResultSyncResult
{
    public int CandidatesMarkedFailed { get; set; }
    public int CandidatesMarkedTested { get; set; }

    /// <summary>Already-Tested candidates (from before this field existed, or from a prior code version) that only got InitialLicenseClass/NewLicenseClass filled in on this pass — not a new pass/fail result.</summary>
    public int CandidatesBackfilledLicenseClass { get; set; }

    public override string ToString() =>
        $"marked Failed {CandidatesMarkedFailed}, marked Tested (passed) {CandidatesMarkedTested}, backfilled license class {CandidatesBackfilledLicenseClass}";
}
