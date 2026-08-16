using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.Reconciliation;

namespace VeSessionManager.Worker;

/// <summary>
/// Nightly check that ExamTools and this database still agree (built 2026-08-10) — see
/// docs/reconciliation.md.
///
/// <para><b>Why a job and not a test.</b> Every other check in this repo runs against fakes that
/// share our own assumptions. This one needs the real feed and real credentials, so it cannot gate a
/// PR; it is a monitor, and it reports after the fact rather than preventing anything. That is worth
/// having anyway: the bug it was built for — the historical import dropping the last day of every
/// calendar month — had a full suite of passing tests, all of which asserted against a fake that
/// shared the wrong assumption about the date bound.</para>
///
/// <para><b>Per team, one JobRunHistory entry each</b>, matching SessionIngestionJob. One team's
/// expired credentials must not hide another team's clean sweep, and the ops dashboard should be
/// able to say which team drifted.</para>
///
/// <para>Daily. The data it checks changes at most once a session, and a discrepancy that has been
/// true for months is not more urgent for being noticed at noon rather than midnight.</para>
///
/// <para>The scan-every-team-in-its-own-scope loop was written out here in full until 2026-08-16
/// (#309, DUP-11) — a verbatim second copy of <see cref="PerTeamDailyJob"/>, down to the comment
/// explaining the per-team scope. It now uses the base, which is also how it keeps its
/// ResultSummary: see the base's <c>RunForTeamAsync</c> for why that was not free.</para>
/// </summary>
public class ReconciliationJob(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<ReconciliationJob> logger)
    : PerTeamDailyJob(scopeFactory, configuration, logger, JobSchedules.Reconciliation)
{
    protected override async Task<object?> RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken) =>
        await scopedServices.GetRequiredService<ReconciliationService>().RunAsync(team, cancellationToken);
}
