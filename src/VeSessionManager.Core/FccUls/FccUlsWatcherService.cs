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
///   - A license match only counts if its Grant Date falls on or after the candidate's own
///     Session.ScheduledStartUtc — same "stale record" gotcha as the application-file rule below,
///     extended to licenses (2026-07-30, resolved with real sample data). An "upgrade exam"
///     candidate (already licensed, testing to move up a class) has had an Active record in the
///     license file the whole time, from their *original* grant — with no session-date guard, that
///     stale record would immediately mark them Granted the moment any watcher run touched their
///     row, even though FCC hasn't processed today's upgrade at all yet. Confirmed live 2026-07-30
///     against three real same-day upgrade candidates (Erik Nielsen, Katelynn Schneider, Zachary
///     Coffey) all showing Grant Dates weeks-to-years before their actual session.
///   - **Upgrades are confirmed via AM.dat + Last Action Date (2026-07-30).** The guard above,
///     shipped alone, made upgrades permanently undetectable — 20 real candidates sat pending, the
///     oldest for 19 days. The missing signal turned out to exist after all: AM.dat (present in both
///     the daily and weekly-complete archives, now read by FccUlsClient) carries the current operator
///     class, and while FCC pins Grant Date to the original license, HD's **Last Action Date does**
///     advance on the upgrade. So an upgrade counts as granted only when the class FCC now reports
///     equals Candidate.NewLicenseClass *and* Last Action Date is on/after the session — two
///     independent confirmations. Verified against six real stuck candidates before shipping, and
///     confirmed to still correctly reject a same-day upgrade FCC hadn't processed yet (Katelynn
///     Schneider: class still Technician, last action predating her session — rejected on both
///     counts). Without AM.dat in a given archive, OperatorClass is None and the old
///     stays-pending behavior applies.
///   - Terminal statuses (Granted/Failed/NotTested) and candidates with a null Frn are excluded by
///     the queries below, not by an explicit check — they're just never selected again.
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

    /// <summary>
    /// Checks both today's and yesterday's day-name file, not just today's — found live 2026-07-30
    /// via a real missed grant (FRN 0038641205/KR4NZD): each day-name file keeps accumulating that
    /// weekday's own transactions until the day passes, so a grant FCC publishes later in the day
    /// than this job's one same-day check (the job only runs once or twice a day, see
    /// FccDailyWatcherJob) is otherwise gone forever the moment "today" rolls over to the next
    /// day-name — there is no other same-week path back to it (FccWeeklyCatchupJob's "complete"
    /// snapshot lags too far behind, see that job's own remarks). Re-checking yesterday's file is a
    /// cheap, idempotent no-op the rest of the time (FccUlsWatcherService only ever touches
    /// non-terminal candidates). See docs/fcc-uls-watcher.md.
    /// </summary>
    public async Task<FccUlsWatchResult> RunDailyAsync(CancellationToken cancellationToken)
    {
        // Eastern time, not raw UTC: FccDailyWatcherJob's evening retry (default 8pm ET) lands
        // at/after UTC midnight for most of the year, which would otherwise resolve to tomorrow's
        // DayOfWeek and fetch the wrong (not-yet-published) file. See docs/fcc-uls-watcher.md.
        var today = TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, FccUlsSchedule.EasternTimeZone).DayOfWeek;
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);

        var yesterdayResult = await RunForDayAsync(yesterday, cancellationToken);
        var todayResult = await RunForDayAsync(today, cancellationToken);

        return new FccUlsWatchResult
        {
            CandidatesMarkedReceived = yesterdayResult.CandidatesMarkedReceived + todayResult.CandidatesMarkedReceived,
            CandidatesMarkedGranted = yesterdayResult.CandidatesMarkedGranted + todayResult.CandidatesMarkedGranted,
            ApplicationFileAvailable = yesterdayResult.ApplicationFileAvailable || todayResult.ApplicationFileAvailable,
            LicenseFileAvailable = yesterdayResult.LicenseFileAvailable || todayResult.LicenseFileAvailable
        };
    }

    /// <summary>
    /// Same daily scan as RunDailyAsync, but for an explicitly-named day rather than "today" —
    /// each day-name URL only ever holds that weekday's most recent transactions, so this is the
    /// only way to recover a specific missed day once "today" has moved past it (short of waiting
    /// for the next weekly catchup's full snapshot to be regenerated). Added 2026-07-30 for exactly
    /// that case: a Worker outage meant Tuesday's file was never fetched on Tuesday, and the most
    /// recent weekly snapshot predated it too.
    /// </summary>
    public Task<FccUlsWatchResult> RunForDayAsync(DayOfWeek day, CancellationToken cancellationToken) =>
        RunAsync(
            () => fccUlsClient.DownloadDailyApplicationsAsync(day, cancellationToken),
            () => fccUlsClient.DownloadDailyLicensesAsync(day, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Sweeps **every** day-name file (Mon-Sat), not just yesterday/today. This is the only thing
    /// that closes the gap between the weekly snapshot's age and now.
    ///
    /// <para>Found live 2026-07-30: the weekly "complete" snapshot is not a rolling backstop the way
    /// its name suggests — it is regenerated roughly weekly and stamps its own creation date inside
    /// the zip. The one fetched that Thursday evening read <c>File Creation Date: Sun Jul 26</c> with
    /// no data newer than 07/25, i.e. it was already 4-5 days stale on arrival. Meanwhile
    /// RunDailyAsync only ever reads yesterday + today. Anything FCC acted on in between — Monday's
    /// and Tuesday's files here — is in **no file either path reads**, and stays invisible until the
    /// next weekly snapshot is cut. Three real upgrade candidates sat pending for exactly this
    /// reason, with the correct data sitting in l_am_mon.zip/l_am_tue.zip the whole time.</para>
    ///
    /// <para>Cheap: each day file is tens of KB (vs. ~199 MB for the weekly). Sunday is skipped —
    /// FCC publishes Tue-Sat covering Mon-Fri activity, so there is no Sunday transaction file.
    /// A day whose file 404s or is simply stale is a normal no-op, same as any other run.</para>
    /// </summary>
    public async Task<FccUlsWatchResult> RunAllDailyFilesAsync(CancellationToken cancellationToken)
    {
        var combined = new FccUlsWatchResult();

        foreach (var day in FccUlsSchedule.PublishedDays)
        {
            var dayResult = await RunForDayAsync(day, cancellationToken);
            combined.CandidatesMarkedReceived += dayResult.CandidatesMarkedReceived;
            combined.CandidatesMarkedGranted += dayResult.CandidatesMarkedGranted;
            combined.ApplicationFileAvailable |= dayResult.ApplicationFileAvailable;
            combined.LicenseFileAvailable |= dayResult.LicenseFileAvailable;
        }

        return combined;
    }

    /// <summary>
    /// The weekly "complete" snapshot **plus** a sweep of every daily file. The snapshot alone is
    /// structurally incapable of being a catch-up backstop — it can be up to a week stale on arrival
    /// (see RunAllDailyFilesAsync) — so the daily sweep is what actually makes this a backstop rather
    /// than merely a re-scan of data the daily job already had. The snapshot still earns its place:
    /// it carries every active license regardless of grant date, which is the only way to recover a
    /// candidate whose day-name file has since been overwritten by the following week's.
    /// </summary>
    public async Task<FccUlsWatchResult> RunWeeklyCatchupAsync(CancellationToken cancellationToken)
    {
        var snapshotResult = await RunAsync(
            () => fccUlsClient.DownloadWeeklyApplicationsAsync(cancellationToken),
            () => fccUlsClient.DownloadWeeklyLicensesAsync(cancellationToken),
            cancellationToken);

        var dailySweepResult = await RunAllDailyFilesAsync(cancellationToken);

        return new FccUlsWatchResult
        {
            CandidatesMarkedReceived = snapshotResult.CandidatesMarkedReceived + dailySweepResult.CandidatesMarkedReceived,
            CandidatesMarkedGranted = snapshotResult.CandidatesMarkedGranted + dailySweepResult.CandidatesMarkedGranted,
            ApplicationFileAvailable = snapshotResult.ApplicationFileAvailable || dailySweepResult.ApplicationFileAvailable,
            LicenseFileAvailable = snapshotResult.LicenseFileAvailable || dailySweepResult.LicenseFileAvailable
        };
    }

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

        // Unmatched-or-Received (not just Unmatched): FccHoldReason needs refreshing every run even
        // for a candidate already marked Received in a prior run — a Red Light/Basic Qualification
        // hold can be placed or cleared well after the initial application match.
        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => (c.ApplicationStatus == CandidateApplicationStatus.Unmatched || c.ApplicationStatus == CandidateApplicationStatus.Received)
                        && c.Frn != null)
            .ToListAsync(cancellationToken);

        var anyChanges = false;
        foreach (var candidate in candidates)
        {
            if (candidate.Frn is null
                || !recordByFrn.TryGetValue(candidate.Frn, out var record)
                || record.LastActionDateUtc.Date < candidate.Session.ScheduledStartUtc.Date)
            {
                continue;
            }

            if (candidate.FccHoldReason != record.HoldReason)
            {
                candidate.FccHoldReason = record.HoldReason;
                anyChanges = true;
            }

            if (candidate.FccPaymentStatus != record.PaymentStatus)
            {
                candidate.FccPaymentStatus = record.PaymentStatus;
                anyChanges = true;
            }

            if (candidate.ApplicationStatus == CandidateApplicationStatus.Unmatched)
            {
                candidate.ApplicationStatus = CandidateApplicationStatus.Received;
                candidate.ApplicationDateEnteredUtc = record.LastActionDateUtc;
                result.CandidatesMarkedReceived++;
                anyChanges = true;
                logger.LogInformation("Candidate {CandidateId} FRN matched in FCC application file — marked Received", candidate.Id);
            }
        }

        if (anyChanges)
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
            .Include(c => c.Session)
            .Where(c => (c.ApplicationStatus == CandidateApplicationStatus.Unmatched || c.ApplicationStatus == CandidateApplicationStatus.Received)
                        && c.Frn != null)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (candidate.Frn is null || !recordByFrn.TryGetValue(candidate.Frn, out var record))
            {
                continue;
            }

            // A first-time licensee gets a brand-new record, so Grant Date itself proves this sitting
            // caused it. Unchanged from the original rule — this path is what already worked.
            var isNewLicense = record.GrantDateUtc.Date >= candidate.Session.ScheduledStartUtc.Date;

            // An upgrade never moves Grant Date (FCC pins it to the original license — a 2026 upgrade
            // can still report a 2021 Grant Date, verified against real data), which is why the
            // Grant-Date-only rule left every upgrade stuck Unmatched/Received forever. Confirm it two
            // independent ways instead: the class they now hold is the one they tested for, AND FCC
            // touched the record on/after the exam. Either alone is insufficient — the class alone
            // would re-confirm a licensee who already held it walking in, and the date alone would
            // match any unrelated administrative action.
            var isConfirmedUpgrade =
                candidate.NewLicenseClass is not null
                && record.OperatorClass == candidate.NewLicenseClass
                && record.LastActionDateUtc.Date >= candidate.Session.ScheduledStartUtc.Date;

            if (!isNewLicense && !isConfirmedUpgrade)
            {
                continue;
            }

            candidate.ApplicationStatus = CandidateApplicationStatus.Granted;
            candidate.CallSign = record.CallSign;
            // For an upgrade, Grant Date is the *original* license's — showing it would read as
            // "licensed in 2021" for a 2026 upgrade. Last Action Date is when the upgrade actually
            // landed, which is what every UI surface using this field is asking about.
            candidate.LicenseGrantDateUtc = isNewLicense ? record.GrantDateUtc : record.LastActionDateUtc;
            candidate.FccUlsLicenseKey = record.UniqueSystemIdentifier;
            result.CandidatesMarkedGranted++;
            logger.LogInformation(
                "Candidate {CandidateId} FRN matched in FCC license file ({MatchKind}) — marked Granted with call sign {CallSign}",
                candidate.Id, isNewLicense ? "new license" : "class upgrade", record.CallSign);
        }

        if (result.CandidatesMarkedGranted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
