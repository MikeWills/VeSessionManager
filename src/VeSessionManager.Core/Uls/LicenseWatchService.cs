using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Uls;

/// <summary>
/// Refreshes every team's watched licences from ExamTools' ULS mirror — see docs/renewal-monitor.md.
///
/// <para>Same scan-based, idempotent shape as every other job here: the work is "re-derive current
/// state from the remote feed", <see cref="WatchedLicense.LastCheckedUtc"/> is both the staleness
/// filter and the progress marker, and each row is saved as it is processed so a crash mid-run keeps
/// whatever it already did.</para>
///
/// <para><b>Renewal detection is a two-step state machine, not a single reading.</b> ULS reports
/// that a renewal application is pending; it never reports that one was *issued*. A renewal leaves
/// the call sign, the operator class and the grant date untouched — the only thing that moves is the
/// expiration date. So the service records the expiration as it stood when the renewal was first
/// seen, and only claims the renewal landed once the current expiration is actually past that
/// stored value.</para>
/// </summary>
public class LicenseWatchService(
    AppDbContext dbContext,
    IUlsLookupClient lookupClient,
    TimeProvider timeProvider,
    ILogger<LicenseWatchService> logger)
{
    /// <summary>
    /// How stale a row may be before it is refreshed. A licence term is ten years and a renewal
    /// takes days to weeks, so nothing here changes hour to hour — daily is already generous, and
    /// the endpoint is a third party's undocumented mirror that this app should lean on gently.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(20);

    /// <summary>
    /// Ceiling on lookups per run, so a team that pastes in several hundred call signs cannot turn
    /// one tick into a burst against ExamTools. The remainder are simply picked up by the next run —
    /// least-recently-checked first, so nothing can be starved indefinitely.
    /// </summary>
    public const int MaxLookupsPerRun = 100;

    public async Task<LicenseWatchResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = new LicenseWatchResult();
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var staleBefore = utcNow - RefreshInterval;

        // Never-checked rows sort first (null is less than any value in SQLite's ordering), which is
        // what makes a just-added licence resolve on the next tick rather than queueing behind a
        // backlog of routine refreshes.
        var due = await dbContext.WatchedLicenses
            .Where(w => w.LastCheckedUtc == null || w.LastCheckedUtc < staleBefore)
            .OrderBy(w => w.LastCheckedUtc)
            .Take(MaxLookupsPerRun)
            .ToListAsync(cancellationToken);

        result.Due = due.Count;

        foreach (var licence in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Call sign is preferred over FRN: it is what the row is keyed on and what a human
            // entered, and the endpoint resolves either.
            var lookup = await lookupClient.LookupByFrnAsync(licence.CallSign, cancellationToken);
            if (lookup is null)
            {
                // The lookup itself failed, so nothing was learned. Deliberately does NOT stamp
                // LastCheckedUtc — leaving it stale is what makes the next run retry this row.
                result.LookupFailures++;
                continue;
            }

            Apply(licence, lookup, utcNow, result);

            // Per row, not batched — same reasoning as UlsWatcherService.
            await dbContext.SaveChangesAsync(cancellationToken);
            result.Checked++;
        }

        logger.LogInformation("Licence watch finished: {Result}", result);
        return result;
    }

    /// <summary>
    /// Overwrites the cached FCC fields wholesale and then advances the renewal state machine. Split
    /// out so the Web add flow can populate a brand-new row from the lookup it already performed —
    /// otherwise the two would map the same response independently and drift — and so the tests can
    /// drive it directly against a fabricated <see cref="UlsLookupResult"/>.
    /// </summary>
    public static void Apply(WatchedLicense licence, UlsLookupResult lookup, DateTime utcNow, LicenseWatchResult result)
    {
        licence.LastCheckedUtc = utcNow;

        if (!lookup.Found)
        {
            // A call sign that resolved when it was added can stop resolving later (a cancelled
            // record eventually drops out). Flag it rather than blanking the row, so the page can
            // say what happened instead of showing an empty line.
            licence.NotFoundAtFcc = true;
            result.NotFound++;
            return;
        }

        licence.NotFoundAtFcc = false;
        licence.LicenseeName = lookup.LicenseeName;
        licence.LicenseStatus = lookup.LicenseStatus;
        licence.OperatorClass = lookup.OperatorClass;
        licence.GrantDateUtc = lookup.GrantDateUtc;
        licence.CancellationDateUtc = lookup.CancellationDateUtc;

        // Keep whatever FRN FCC reports — this is how a row added by call sign acquires one.
        if (!string.IsNullOrWhiteSpace(lookup.Frn)) licence.Frn = lookup.Frn;

        // A call sign can change (vanity), and the row is keyed on it, so follow FCC rather than
        // pinning to what was typed. Uniqueness is per team, so a rename colliding with another
        // watched row would throw on save — accepted as vanishingly rare and loud rather than
        // silently dropping one of the two.
        if (!string.IsNullOrWhiteSpace(lookup.CallSign)) licence.CallSign = lookup.CallSign;

        var previousExpiry = licence.ExpiredDateUtc;
        licence.ExpiredDateUtc = lookup.ExpiredDateUtc;

        ApplyRenewalState(licence, lookup, utcNow, previousExpiry, result);
    }

    /// <summary>
    /// The request-through-issuance half.
    ///
    /// <para><b>Confirmation is checked before the still-pending branch.</b> FCC does not
    /// necessarily drop the application from <c>pendingApplications</c> the instant the new term is
    /// granted — the two can overlap for a while. Testing "did the expiration advance?" first means
    /// an overlap reports Renewed rather than sticking on Renewal pending until FCC tidies up.</para>
    /// </summary>
    private static void ApplyRenewalState(
        WatchedLicense licence,
        UlsLookupResult lookup,
        DateTime utcNow,
        DateTime? previousExpiry,
        LicenseWatchResult result)
    {
        var renewal = lookup.PendingApplications.FirstOrDefault(a => a.IsRenewal);

        if (licence.RenewalPendingSinceUtc is not null)
        {
            var filedAgainst = licence.ExpiredDateWhenRenewalFiledUtc;
            var issued = licence.ExpiredDateUtc is { } current &&
                         (filedAgainst is null || current > filedAgainst);

            if (issued)
            {
                licence.RenewalConfirmedUtc = utcNow;
                licence.RenewalPendingSinceUtc = null;
                licence.RenewalFileNumber = null;
                licence.ExpiredDateWhenRenewalFiledUtc = null;
                result.RenewalsConfirmed++;
                return;
            }

            if (renewal is null)
            {
                // The application vanished without the expiration moving — dismissed, withdrawn, or
                // FCC re-filed it under a new number. Reset to "not pending" so a later application
                // is seen as new; the row simply goes back to reporting its real expiry.
                licence.RenewalPendingSinceUtc = null;
                licence.RenewalFileNumber = null;
                licence.ExpiredDateWhenRenewalFiledUtc = null;
                result.RenewalsAbandoned++;
                return;
            }

            // Still pending — refresh the file number in case FCC re-issued it, but leave
            // RenewalPendingSinceUtc alone: it records when *we* first saw it, and must not creep
            // forward on every poll.
            licence.RenewalFileNumber = renewal.UlsFileNumber ?? licence.RenewalFileNumber;
            return;
        }

        if (renewal is not null)
        {
            licence.RenewalPendingSinceUtc = utcNow;
            licence.RenewalFileNumber = renewal.UlsFileNumber;
            // Anchor on the expiry as it stands right now — the value the renewal must beat. Falls
            // back to the pre-refresh value if this poll returned none, so the anchor is never null
            // when we had something to record.
            licence.ExpiredDateWhenRenewalFiledUtc = licence.ExpiredDateUtc ?? previousExpiry;
            result.RenewalsDetected++;
        }
    }
}

public class LicenseWatchResult
{
    /// <summary>How many rows were due for a refresh this run (capped at <see cref="LicenseWatchService.MaxLookupsPerRun"/>).</summary>
    public int Due { get; set; }

    /// <summary>Rows successfully looked up and updated.</summary>
    public int Checked { get; set; }

    /// <summary>Lookups that could not be performed at all — these keep their stale timestamp and retry next run.</summary>
    public int LookupFailures { get; set; }

    public int NotFound { get; set; }
    public int RenewalsDetected { get; set; }
    public int RenewalsConfirmed { get; set; }
    public int RenewalsAbandoned { get; set; }

    public override string ToString() =>
        $"{Checked}/{Due} checked, {LookupFailures} lookup failure(s), {NotFound} not found, " +
        $"renewals: {RenewalsDetected} detected / {RenewalsConfirmed} confirmed / {RenewalsAbandoned} abandoned";
}
