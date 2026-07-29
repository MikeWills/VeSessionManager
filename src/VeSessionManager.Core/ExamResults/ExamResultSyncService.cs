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
/// don't need it anymore.
/// </summary>
public class ExamResultSyncService(
    AppDbContext dbContext,
    IExamToolsClient examToolsClient,
    TimeProvider timeProvider,
    IOptions<ExamToolsOptions> examToolsOptions,
    ILogger<ExamResultSyncService> logger)
{
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

        var sessions = await dbContext.Sessions
            .Include(s => s.Candidates)
            .Where(s => s.TeamId == team.Id && s.Status == SessionStatus.Active && s.ScheduledStartUtc <= now)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            var pendingCandidates = session.Candidates
                .Where(c => !c.Tested && !c.ApplicationStatus.IsTerminal() && c.ExamToolsApplicantId is not null)
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

        logger.LogInformation("Exam result sync finished for team {TeamId} ({TeamName}): {Result}", team.Id, team.Name, result);
        return result;
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
            candidate.Tested = true;
            result.CandidatesMarkedTested++;
        }
    }
}

public class ExamResultSyncResult
{
    public int CandidatesMarkedFailed { get; set; }
    public int CandidatesMarkedTested { get; set; }

    public override string ToString() =>
        $"marked Failed {CandidatesMarkedFailed}, marked Tested (passed) {CandidatesMarkedTested}";
}
