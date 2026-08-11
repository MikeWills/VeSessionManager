using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Uls;

/// <summary>
/// Matches non-terminal candidates against ExamTools' ULS mirror, one FRN lookup each. Replaced the
/// FCC bulk-file watcher on 2026-07-31 — see docs/uls-watcher.md for why, and CHANGELOG.md for what
/// was removed. Scan-based and idempotent like every other job here: the ApplicationStatus
/// transition is itself the "already handled" guard, so nothing is double-processed on a retry.
///
/// <para>**The grant rules are carried over unchanged from the FCC watcher** — only the data source
/// moved. Both were bought with real incidents and neither may be relaxed:</para>
/// <list type="bullet">
///   <item>Only <c>license_status: "Active"</c> counts (the old HD License Status "A" rule). The same
///   FRN can carry a Canceled/Expired record touched by unrelated administrative activity.</item>
///   <item>A **new license** counts only when its grant date is on/after the candidate's session —
///   without that guard an upgrade candidate's *pre-existing* license marks them Granted instantly,
///   which is exactly what wrongly granted three real candidates on 2026-07-30.</item>
///   <item>An **upgrade** counts only when the class ULS now reports equals Candidate.NewLicenseClass
///   **and** the effective date is on/after the session. Either alone is insufficient: class alone
///   re-confirms someone who already held it walking in, date alone matches any unrelated action.
///   Grant date is useless here — FCC pins it to the original license and never advances it on an
///   upgrade, which is what left 20 real candidates stuck pending for up to 19 days.</item>
/// </list>
///
/// <para>`effective_date` is ExamTools' rendering of HD's Last Action Date — verified 2026-07-31
/// against a real upgrade whose grant date read 2024-08-21 while effective date read 2026-07-30,
/// the session date.</para>
///
/// <para>**Application data is informational only.** Received/hold/fee come from the pending
/// application block and are explicitly *not* trusted to the same standard as grants ("I trust the
/// ET license grants, I don't trust the applications"). They drive display, never money or
/// retention decisions — those key off ApplicationStatus and LicenseGrantDateUtc.</para>
/// </summary>
public class UlsWatcherService(
    AppDbContext dbContext,
    IUlsLookupClient lookupClient,
    TimeProvider timeProvider,
    ILogger<UlsWatcherService> logger)
{
    private const string ActiveLicenseStatus = "Active";

    /// <summary>
    /// Ceiling per run (issue #247, 2026-08-11). Both sibling sweeps —
    /// <see cref="LicenseWatchService"/> and <see cref="VolunteerExaminerLicenseWatchService"/> —
    /// have carried one since they were written; this one did not, so a growing backlog of
    /// never-resolving candidates meant an unbounded burst of sequential calls against a third
    /// party's undocumented mirror, twice a day, forever.
    ///
    /// <para>250 matches the siblings. It is far above the live scan (about a dozen non-terminal
    /// candidates carry an FRN today) — this is a ceiling, not a throttle, and if it ever starts
    /// binding that is itself the signal worth noticing.</para>
    /// </summary>
    public const int MaxLookupsPerRun = 250;

    public async Task<UlsWatchResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = new UlsWatchResult();
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        // Terminal statuses and null-FRN candidates are excluded by the query rather than an
        // explicit check — they are simply never selected again. Expressed as "not terminal" using
        // the shared definition, rather than by listing the two non-terminal values: a new
        // non-terminal status would otherwise have to be remembered here, and forgetting it means
        // those candidates are silently never watched. Translates to SQL NOT IN.
        //
        // Ordered least-recently-attempted first (nulls, i.e. never attempted, sort first) and
        // capped — see MaxLookupsPerRun. The ordering is what makes the cap fair rather than a
        // permanent cliff: whoever misses this run leads the next one.
        var candidates = await dbContext.Candidates
            .Include(c => c.Session)
            .Where(c => !CandidateApplicationStatusExtensions.TerminalStatuses.Contains(c.ApplicationStatus)
                        && c.Frn != null)
            .OrderBy(c => c.UlsLastCheckedUtc)
            .Take(MaxLookupsPerRun)
            .ToListAsync(cancellationToken);

        result.CandidatesChecked = candidates.Count;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = await lookupClient.LookupByFrnAsync(candidate.Frn!, cancellationToken);

            // Stamped before the outcome is examined, and on every path including failure. A row
            // that is never stamped keeps null, null sorts first, and it would lead the queue
            // forever — starving everyone behind it. Costing a failed lookup one cycle of delay is
            // the cheaper mistake.
            candidate.UlsLastCheckedUtc = utcNow;
            var changed = true;

            if (lookup is null)
            {
                // Lookup itself failed — learned nothing about the candidate. Everything except the
                // attempt stamp is left untouched so the next run retries; do not confuse this with
                // "FCC has no record".
                result.LookupFailures++;
            }
            else if (lookup.Found)
            {
                changed |= ApplyLicenseKey(candidate, lookup);
                changed |= ApplyPendingApplication(candidate, lookup);
                changed |= ApplyGrant(candidate, lookup, result);
            }

            if (changed)
            {
                // Saved per-candidate, not batched: a crash mid-scan must never lose progress
                // already made, same as every other scan-based job in this codebase.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        logger.LogInformation("ULS watch finished: {Result}", result);
        return result;
    }

    /// <summary>
    /// Persists the ULS license key (`u_id`) whenever ULS reports one — **not only on grant**.
    ///
    /// <para>An upgrade candidate already holds a license while their upgrade is still pending, so
    /// capturing the key early gives Applicant Status a working "view in FCC ULS" link for the whole
    /// waiting period rather than only after the grant lands. Verified 2026-07-31 that `u_id` is
    /// exactly the `licKey` FCC's own URL takes
    /// (`UlsSearch/license.jsp?licKey=5339575` ⇔ `u_id: 5339575` for FRN 0038616330).</para>
    ///
    /// <para>For a first-time applicant there is no license yet, so this stays null until the grant —
    /// which is precisely why the link is rendered conditionally.</para>
    /// </summary>
    private static bool ApplyLicenseKey(Candidate candidate, UlsLookupResult lookup)
    {
        var key = lookup.UniqueSystemIdentifier?.ToString();
        if (key is null || candidate.FccUlsLicenseKey == key)
        {
            return false;
        }

        candidate.FccUlsLicenseKey = key;
        return true;
    }

    /// <summary>
    /// Informational half. Mirrors the old application-file rule: a pending application only counts
    /// if FCC received it on/after the candidate's own session — a genuinely new application cannot
    /// predate the exam that produced it, and without this an old dismissed application sharing the
    /// same FRN would mark them Received.
    /// </summary>
    private static bool ApplyPendingApplication(Candidate candidate, UlsLookupResult lookup)
    {
        var application = lookup.PendingApplications
            .Where(a => a.ReceiptDateUtc is not null)
            .OrderByDescending(a => a.ReceiptDateUtc)
            .FirstOrDefault();

        // ToEasternDate, not .Date — the session's timestamp is an instant, FCC's receipt date is a
        // wall-clock date stamped at UTC midnight. See #248 and UlsSchedule.ToEasternDate.
        if (application?.ReceiptDateUtc is not { } receiptDate
            || receiptDate.Date < UlsSchedule.ToEasternDate(candidate.Session.ScheduledStartUtc))
        {
            return false;
        }

        var changed = false;
        var holdReason = ResolveHoldReason(application);
        if (candidate.FccHoldReason != holdReason)
        {
            candidate.FccHoldReason = holdReason;
            changed = true;
        }

        var paymentStatus = ResolvePaymentStatus(application);
        if (candidate.FccPaymentStatus != paymentStatus)
        {
            candidate.FccPaymentStatus = paymentStatus;
            changed = true;
        }

        if (candidate.ApplicationStatus == CandidateApplicationStatus.Unmatched)
        {
            candidate.ApplicationStatus = CandidateApplicationStatus.Received;
            candidate.ApplicationDateEnteredUtc = receiptDate;
            changed = true;
        }

        if (candidate.UlsApplicationFileNumber != application.UlsFileNumber)
        {
            candidate.UlsApplicationFileNumber = application.UlsFileNumber;
            changed = true;
        }

        return changed;
    }

    private bool ApplyGrant(Candidate candidate, UlsLookupResult lookup, UlsWatchResult result)
    {
        if (!string.Equals(lookup.LicenseStatus, ActiveLicenseStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // ToEasternDate, not .Date. Both dates below are FCC wall-clock dates stamped at UTC
        // midnight, so the session instant has to be reduced to *its* Eastern calendar date or an
        // evening-ET session compares against tomorrow. See #248.
        var sessionDate = UlsSchedule.ToEasternDate(candidate.Session.ScheduledStartUtc);

        var isNewLicense = lookup.GrantDateUtc is { } grantDate && grantDate.Date >= sessionDate;

        var isConfirmedUpgrade =
            candidate.NewLicenseClass is not null
            && lookup.OperatorClass == candidate.NewLicenseClass
            && lookup.EffectiveDateUtc is { } effectiveDate
            && effectiveDate.Date >= sessionDate;

        if (!isNewLicense && !isConfirmedUpgrade)
        {
            return false;
        }

        candidate.ApplicationStatus = CandidateApplicationStatus.Granted;
        candidate.CallSign = lookup.CallSign;
        // For an upgrade the grant date is the *original* license's — surfacing it would read as
        // "licensed in 2021" for a 2026 upgrade. Effective date is when the upgrade actually landed,
        // which is what every UI using this field is asking about.
        candidate.LicenseGrantDateUtc = isNewLicense ? lookup.GrantDateUtc : lookup.EffectiveDateUtc;
        // FccUlsLicenseKey is deliberately NOT set here — ApplyLicenseKey already ran this pass and
        // captures it whether or not the grant confirms. One assignment, one place.
        result.CandidatesMarkedGranted++;

        logger.LogInformation(
            "Candidate {CandidateId} matched in ULS ({MatchKind}) — marked Granted with call sign {CallSign}",
            candidate.Id, isNewLicense ? "new license" : "class upgrade", lookup.CallSign);

        return true;
    }

    /// <summary>OFF/COM toggle walked in the history's own order, same as the old HS.dat rule — an OFF with no later COM means the hold is still open.</summary>
    private static FccApplicationHoldReason ResolveHoldReason(UlsPendingApplication application)
    {
        var redLight = IsOpen(application, "RDLOFF", "RDLCOM");
        var basicQualification = IsOpen(application, "BQOFF", "BQCOM");

        return (redLight, basicQualification) switch
        {
            (true, true) => FccApplicationHoldReason.RedLightAndBasicQualification,
            (true, false) => FccApplicationHoldReason.RedLight,
            (false, true) => FccApplicationHoldReason.BasicQualification,
            _ => FccApplicationHoldReason.None
        };
    }

    private static FccApplicationPaymentStatus ResolvePaymentStatus(UlsPendingApplication application)
    {
        var codes = application.History.Select(h => h.Code).ToList();

        if (codes.Contains("FVPCNF") || codes.Contains("FVPCOM"))
        {
            return FccApplicationPaymentStatus.Paid;
        }

        return codes.Contains("FVPOFF")
            ? FccApplicationPaymentStatus.PendingVerification
            : FccApplicationPaymentStatus.Unknown;
    }

    private static bool IsOpen(UlsPendingApplication application, string offCode, string completeCode)
    {
        var open = false;
        foreach (var entry in application.History)
        {
            if (entry.Code == offCode)
            {
                open = true;
            }
            else if (entry.Code == completeCode)
            {
                open = false;
            }
        }

        return open;
    }
}
