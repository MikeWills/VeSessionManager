using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.ArrlSubmissions;

/// <summary>
/// Phase 8's "dashboard indicator: count of sessions pending ARRL submission." A session counts as
/// pending when it has at least one candidate whose ApplicationStatus has reached a terminal/
/// complete state (Granted, Failed, or NotTested — the same terminal set SessionIngestionService
/// already treats as "done," meaning there's something concrete to submit) but the session's own
/// ArrlSubmissionStatus is still NotSubmitted. Cancelled sessions never count — nothing to submit
/// for a session that never happened. No UI yet (Phase 9); a future admin dashboard calls this
/// directly.
/// </summary>
public class ArrlSubmissionReportService(AppDbContext dbContext)
{
    public async Task<int> GetPendingSubmissionCountAsync(int teamId, CancellationToken cancellationToken) =>
        await dbContext.Sessions
            .Where(s => s.TeamId == teamId
                && s.Status == SessionStatus.Active
                && s.ArrlSubmissionStatus == ArrlSubmissionStatus.NotSubmitted
                && s.Candidates.Any(c =>
                    c.ApplicationStatus == CandidateApplicationStatus.Granted
                    || c.ApplicationStatus == CandidateApplicationStatus.Failed
                    || c.ApplicationStatus == CandidateApplicationStatus.NotTested))
            .CountAsync(cancellationToken);
}
