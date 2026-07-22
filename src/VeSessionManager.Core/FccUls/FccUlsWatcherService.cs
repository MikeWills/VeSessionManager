using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.FccUls;

/// <summary>
/// Phase 5: matches candidates against FCC's daily amateur application/license transaction files
/// by FRN, scan-based like every other phase's service — no event queue, just re-reading
/// Candidate.ApplicationStatus/Frn each run.
///
///   - Unmatched candidate whose FRN appears in the application file -> Received, with
///     ApplicationDateEnteredUtc taken from HD's Last Action Date (the date ULS recorded this
///     transaction — confirmed against real pending-application data, where every row's Last
///     Action Date equals the file's own transaction day).
///   - An application-file match only counts if its Last Action Date falls on or after the
///     candidate's own Session.ScheduledStartUtc — see the "stale application" gotcha below.
///   - Unmatched OR Received candidate whose FRN appears in the license file with an Active
///     ("A") HD License Status -> Granted, with CallSign/LicenseGrantDateUtc set from that record.
///     License match always wins and short-circuits application status, so Unmatched -> Granted
///     directly is expected, not a bug, when a daily application file was missed but the license
///     file wasn't.
///   - Only License Status "A" counts as a grant: the same FRN can appear in a license file for an
///     unrelated administrative touch on an already-Canceled/Expired record (observed in real ULS
///     data — a Canceled license from years prior still shows up with a same-day Last Action Date
///     from something unrelated). A brand-new candidate has no prior license, so this filter has
///     no effect on the common case; it only guards the edge case.
///   - Terminal statuses (Granted/Failed/NotTested) and candidates with a null Frn are excluded by
///     the queries below, not by an explicit check — they're just never selected again.
///
/// Deliberately does not attempt the "upgrade exam" (existing licensee) case per the spec's Open
/// Item — an existing licensee's FRN could already be in a license file for reasons unrelated to
/// this exam, and telling a real new grant apart from a pre-existing one needs real sample data
/// this phase doesn't have yet.
///
/// Stale/dismissed application gotcha (found 2026-07-22 via a live FRN lookup): the "application"
/// file's HD row has no field distinguishing "genuinely still pending" from "dismissed/withdrawn/
/// returned months ago" — both look identical (blank HD License Status). A candidate's *new*
/// post-session application can therefore share an FRN with an old, already-resolved application
/// still sitting in FCC's "Applications - complete" snapshot. Guarded against by (1) only trusting
/// an application match whose Last Action Date is on/after the candidate's own
/// Session.ScheduledStartUtc — a real new-license application can't have been filed before the
/// exam that produced it — and (2) picking the most recent Last Action Date per FRN when a file
/// contains more than one row for it, rather than an arbitrary "first in file order" pick.
/// </summary>
public class FccUlsWatcherService(
    AppDbContext dbContext,
    IFccUlsClient fccUlsClient,
    TimeProvider timeProvider,
    ILogger<FccUlsWatcherService> logger)
{
    private const string ActiveLicenseStatus = "A";

    public Task<FccUlsWatchResult> RunDailyAsync(CancellationToken cancellationToken)
    {
        var day = timeProvider.GetUtcNow().UtcDateTime.DayOfWeek;
        return RunAsync(
            () => fccUlsClient.DownloadDailyApplicationsAsync(day, cancellationToken),
            () => fccUlsClient.DownloadDailyLicensesAsync(day, cancellationToken),
            cancellationToken);
    }

    public Task<FccUlsWatchResult> RunWeeklyCatchupAsync(CancellationToken cancellationToken) =>
        RunAsync(
            () => fccUlsClient.DownloadWeeklyApplicationsAsync(cancellationToken),
            () => fccUlsClient.DownloadWeeklyLicensesAsync(cancellationToken),
            cancellationToken);

    private async Task<FccUlsWatchResult> RunAsync(
        Func<Task<IReadOnlyList<FccUlsApplicationRecord>?>> downloadApplications,
        Func<Task<IReadOnlyList<FccUlsLicenseRecord>?>> downloadLicenses,
        CancellationToken cancellationToken)
    {
        var result = new FccUlsWatchResult();

        var applications = await downloadApplications();
        if (applications is not null)
        {
            result.ApplicationFileAvailable = true;
            await ProcessApplicationsAsync(applications, result, cancellationToken);
        }

        // Processed second and re-queries the DB, so a candidate marked Received above by the same
        // run is already persisted and eligible for the Unmatched-or-Received Granted check below.
        var licenses = await downloadLicenses();
        if (licenses is not null)
        {
            result.LicenseFileAvailable = true;
            await ProcessLicensesAsync(licenses, result, cancellationToken);
        }

        logger.LogInformation("FCC ULS watch finished: {Result}", result);
        return result;
    }

    private async Task ProcessApplicationsAsync(IReadOnlyList<FccUlsApplicationRecord> records, FccUlsWatchResult result, CancellationToken cancellationToken)
    {
        // OrderByDescending + First (not just First): a stale/dismissed application can share an
        // FRN with a genuinely new one in the same file (see the stale-application gotcha in this
        // class's doc comment) — always prefer whichever row is actually most recent.
        var recordByFrn = records
            .GroupBy(r => r.Frn)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.LastActionDateUtc).First());

        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => c.ApplicationStatus == CandidateApplicationStatus.Unmatched && c.Frn != null)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (candidate.Frn is not null
                && recordByFrn.TryGetValue(candidate.Frn, out var record)
                && record.LastActionDateUtc.Date >= candidate.Session.ScheduledStartUtc.Date)
            {
                candidate.ApplicationStatus = CandidateApplicationStatus.Received;
                candidate.ApplicationDateEnteredUtc = record.LastActionDateUtc;
                result.CandidatesMarkedReceived++;
                logger.LogInformation("Candidate {CandidateId} FRN matched in FCC application file — marked Received", candidate.Id);
            }
        }

        if (result.CandidatesMarkedReceived > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ProcessLicensesAsync(IReadOnlyList<FccUlsLicenseRecord> records, FccUlsWatchResult result, CancellationToken cancellationToken)
    {
        var recordByFrn = records
            .Where(r => r.LicenseStatus == ActiveLicenseStatus)
            .GroupBy(r => r.Frn)
            .ToDictionary(g => g.Key, g => g.First());

        var candidates = await dbContext.Candidates
            .Where(c => (c.ApplicationStatus == CandidateApplicationStatus.Unmatched || c.ApplicationStatus == CandidateApplicationStatus.Received)
                        && c.Frn != null)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (candidate.Frn is not null && recordByFrn.TryGetValue(candidate.Frn, out var record))
            {
                candidate.ApplicationStatus = CandidateApplicationStatus.Granted;
                candidate.CallSign = record.CallSign;
                candidate.LicenseGrantDateUtc = record.GrantDateUtc;
                result.CandidatesMarkedGranted++;
                logger.LogInformation("Candidate {CandidateId} FRN matched in FCC license file — marked Granted with call sign {CallSign}", candidate.Id, record.CallSign);
            }
        }

        if (result.CandidatesMarkedGranted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
