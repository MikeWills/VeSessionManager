using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Uls;

/// <summary>
/// Refreshes every team's watched licenses from ExamTools' ULS mirror — see docs/renewal-monitor.md.
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
    /// How stale a row may be before it is refreshed.
    ///
    /// <para><b>Six hours, not twenty.</b> The original 20 hours came from "a license term is ten
    /// years and a renewal takes days to weeks, so nothing changes hour to hour" — true of the
    /// license, wrong about the feed. FCC posts its daily changes at <b>02:00 ET</b>, so the useful
    /// question is not how fast a license changes but how long after that nightly run this app
    /// notices. At 20 hours the answer drifts: a renewal granted at 02:00 sat invisible until the
    /// following evening simply because the row had last been checked at 21:27 (observed
    /// 2026-08-06). Six hours bounds the lag to a morning while still costing four lookups a day per
    /// license against a third-party mirror.</para>
    ///
    /// <para><b>Since 2026-08-06 the cadence is decided by the job's anchored 06:00 ET slot, not by
    /// this number.</b> Its remaining job is to stop a second run on the same day (a Worker restart,
    /// a manual trigger) redoing every lookup — six hours is comfortably shorter than the daily gap,
    /// so the anchored run always finds every row due.</para>
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Ceiling on lookups per run, so a team that pastes in several hundred call signs cannot turn
    /// one run into a burst against ExamTools. The remainder are picked up next time,
    /// least-recently-checked first, so nothing is starved indefinitely.
    ///
    /// <para>Raised from 100 when the job moved to one anchored run a day: "next time" used to mean
    /// four hours, and now means tomorrow. 250 once a day is still a trivial load on the endpoint,
    /// and covers any watch list a VE team is plausibly going to keep.</para>
    /// </summary>
    public const int MaxLookupsPerRun = 250;

    public async Task<LicenseWatchResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = new LicenseWatchResult();
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var staleBefore = utcNow - RefreshInterval;

        // Never-checked rows sort first (null is less than any value in SQLite's ordering), which is
        // what makes a just-added license resolve on the next tick rather than queueing behind a
        // backlog of routine refreshes.
        var due = await dbContext.WatchedLicenses
            .Where(w => w.LastCheckedUtc == null || w.LastCheckedUtc < staleBefore)
            .OrderBy(w => w.LastCheckedUtc)
            .Take(MaxLookupsPerRun)
            .ToListAsync(cancellationToken);

        result.Due = due.Count;

        foreach (var license in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per row, and isolated per row (issue #249). Apply's own comment says a vanity rename
            // colliding with IX_WatchedLicenses_TeamId_CallSign will throw on save and calls that
            // "loud rather than silently dropping one of the two" — but with no catch here, loud
            // meant abandoning every remaining license in the run. Same shape as
            // VolunteerExaminerLicenseWatchService's per-row guard.
            try
            {
                // Call sign is preferred over FRN: it is what the row is keyed on and what a human
                // entered, and the endpoint resolves either.
                var lookup = await lookupClient.LookupAsync(license.CallSign, cancellationToken);
                if (lookup is null)
                {
                    // The lookup itself failed, so nothing was learned. Deliberately does NOT stamp
                    // LastCheckedUtc — leaving it stale is what makes the next run retry this row.
                    result.LookupFailures++;
                    continue;
                }

                Apply(license, lookup, utcNow, result);

                // Per row, not batched — same reasoning as UlsWatcherService.
                await dbContext.SaveChangesAsync(cancellationToken);
                result.Checked++;
            }
            catch (Exception ex)
            {
                // Scoped detach, NOT ChangeTracker.Clear(). Clearing would detach every other row in
                // `due` as well, so the rest of the run would mutate detached objects and save
                // nothing while still counting itself successful — the bug this sweep's sibling had
                // (issue #231). Only the row that failed is discarded.
                dbContext.Entry(license).State = EntityState.Detached;
                result.Failures++;
                logger.LogError(ex, "Failed to refresh watched license {WatchedLicenseId} ({CallSign})",
                    license.Id, license.CallSign);
            }
        }

        logger.LogInformation("License watch finished: {Result}", result);
        return result;
    }

    /// <summary>
    /// Adds a call sign or FRN to a team's watch list, resolving it against ULS first so the row is
    /// complete the moment it renders instead of reading "not checked yet" until the Worker's next
    /// tick.
    ///
    /// <para><b>This lived in <c>RenewalMonitorModel.OnPostAddAsync</c> until 2026-08-11 (issue
    /// #310).</b> It was 71 lines of lookup, validation, de-duplication, entity construction, audit
    /// and two saves — the only page model in the solution doing real domain work, and the only
    /// reason <see cref="Apply"/> was public. Two consequences of it living there: the
    /// <c>WatchedLicense</c> creation rules existed only on a Razor page, so nothing else could
    /// create one correctly; and the two fixes this file received the same day (per-row isolation
    /// and call-sign normalization) applied to the sweep and not to the add path.</para>
    ///
    /// <para><b>Team authorization is the caller's job, deliberately.</b> This takes a
    /// <paramref name="teamId"/> it trusts. The page resolves that against
    /// <c>SessionAccessScope.GetAvailableTeamsAsync</c> before calling, because the picker only
    /// decides what is worth rendering — the same split every other service here uses.</para>
    /// </summary>
    /// <param name="entry">Call sign or FRN as typed. The ULS endpoint resolves either.</param>
    public async Task<AddWatchedLicenseResult> AddWatchedLicenseAsync(
        int teamId,
        string? entry,
        string? note,
        int addedByUserId,
        CancellationToken cancellationToken)
    {
        var trimmed = entry?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new AddWatchedLicenseResult(AddWatchedLicenseOutcome.CallSignRequired, "");
        }

        var lookup = await lookupClient.LookupAsync(trimmed, cancellationToken);
        if (lookup is null)
        {
            // The endpoint itself was unreachable. Distinct from "no such call sign" — the caller
            // must be able to say so, rather than telling someone their correct call sign does not
            // exist. Same null-means-learned-nothing contract the sweep relies on.
            return new AddWatchedLicenseResult(AddWatchedLicenseOutcome.LookupUnavailable, trimmed.ToUpperInvariant());
        }

        if (!lookup.Found || string.IsNullOrWhiteSpace(lookup.CallSign))
        {
            return new AddWatchedLicenseResult(AddWatchedLicenseOutcome.NotFoundAtFcc, trimmed);
        }

        // NormalizeFormat, not Normalize: this is the value FCC just returned, so it is already a
        // real call sign — and the duplicate check below compares it against stored rows, which were
        // normalized the same way.
        var callSign = CallSign.NormalizeFormat(lookup.CallSign)!;
        if (await dbContext.WatchedLicenses.AnyAsync(
                w => w.TeamId == teamId && w.CallSign == callSign, cancellationToken))
        {
            return new AddWatchedLicenseResult(AddWatchedLicenseOutcome.AlreadyWatched, callSign);
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var license = new WatchedLicense
        {
            TeamId = teamId,
            CallSign = callSign,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            AddedByUserId = addedByUserId,
            AddedUtc = utcNow
        };

        // Reuses the sweep's own mapping so the two cannot drift — the reason Apply was split out in
        // the first place, now expressed as one service calling itself rather than a page reaching in.
        Apply(license, lookup, utcNow, new LicenseWatchResult());

        dbContext.WatchedLicenses.Add(license);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Audited after the save, because EntityId is an int and the row has no id until then.
        dbContext.AddAuditLog(addedByUserId, "Create", nameof(WatchedLicense), license.Id,
            $"Added {callSign} to the license watch list", utcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AddWatchedLicenseResult(AddWatchedLicenseOutcome.Success, callSign, license);
    }

    /// <summary>
    /// Overwrites the cached FCC fields wholesale and then advances the renewal state machine.
    ///
    /// <para><b>Private since 2026-08-11 (issue #310).</b> Its previous doc gave two reasons for
    /// being public — the Web add flow, which now calls <see cref="AddWatchedLicenseAsync"/> above
    /// instead, and "so the tests can drive it directly", which was never true: no test has ever
    /// called it. Both callers are now in this file.</para>
    /// </summary>
    private static void Apply(WatchedLicense license, UlsLookupResult lookup, DateTime utcNow, LicenseWatchResult result)
    {
        license.LastCheckedUtc = utcNow;

        if (!lookup.Found)
        {
            // A call sign that resolved when it was added can stop resolving later (a cancelled
            // record eventually drops out). Flag it rather than blanking the row, so the page can
            // say what happened instead of showing an empty line.
            license.NotFoundAtFcc = true;
            result.NotFound++;
            return;
        }

        license.NotFoundAtFcc = false;
        license.LicenseeName = lookup.LicenseeName;
        license.LicenseStatus = lookup.LicenseStatus;
        license.OperatorClass = lookup.OperatorClass;
        license.GrantDateUtc = lookup.GrantDateUtc;
        license.CancellationDateUtc = lookup.CancellationDateUtc;

        // Keep whatever FRN FCC reports — this is how a row added by call sign acquires one.
        if (!string.IsNullOrWhiteSpace(lookup.Frn)) license.Frn = lookup.Frn;

        // A call sign can change (vanity), and the row is keyed on it, so follow FCC rather than
        // pinning to what was typed. Uniqueness is per team, so a rename colliding with another
        // watched row would throw on save — accepted as vanishingly rare and loud rather than
        // silently dropping one of the two. **The caller catches that per row** (see RunAsync); it
        // used to escape and abandon every remaining license in the run (issue #249).
        //
        // Normalized, not stored raw (issue #286). IX_WatchedLicenses_TeamId_CallSign is a SQLite
        // index and `=` there is case-sensitive, so an unnormalized value lets the same license be
        // watched twice and makes every later match miss. VolunteerExaminerLicenseWatchService
        // already normalized; this one did not.
        var reportedCallSign = CallSign.Normalize(lookup.CallSign);
        if (reportedCallSign is not null) license.CallSign = reportedCallSign;

        var previousExpiry = license.ExpiredDateUtc;
        license.ExpiredDateUtc = lookup.ExpiredDateUtc;

        ApplyRenewalState(license, lookup, utcNow, previousExpiry, result);
    }

    /// <summary>
    /// The request-through-issuance half.
    ///
    /// <para><b>Confirmation is checked before the still-pending branch.</b> FCC does not
    /// necessarily drop the application from <c>pendingApplications</c> the instant the new term is
    /// granted — the two can overlap for a while. Testing "did the expiration advance?" first means
    /// an overlap reports Renewed rather than sticking on Renewal pending until FCC tidies up.</para>
    ///
    /// <para><b>And that overlap outlives the poll that confirmed the renewal</b> — see
    /// <see cref="IsAlreadyIssued"/>. Issuance is terminal for a given application: once the
    /// expiration has moved, the application still sitting in the list is a receipt, not a
    /// request.</para>
    /// </summary>
    private static void ApplyRenewalState(
        WatchedLicense license,
        UlsLookupResult lookup,
        DateTime utcNow,
        DateTime? previousExpiry,
        LicenseWatchResult result)
    {
        var renewalApplications = lookup.PendingApplications.Where(a => a.IsRenewal).ToList();

        // Anything FCC has already acted on is filtered out here rather than tested in each branch
        // below, so no path can mistake a lingering receipt for a live request.
        var renewal = renewalApplications.FirstOrDefault(a => !IsAlreadyIssued(license, a));

        // True when every renewal application on the record is one we have already seen land. Kept
        // apart from "no application at all" so clearing it isn't miscounted as an abandonment.
        var lingeringOnly = renewal is null && renewalApplications.Count > 0;

        // The file number is retained (not cleared) through a confirmation, because it is what
        // IsAlreadyIssued matches the lingering application against on subsequent polls.
        void Confirm()
        {
            license.RenewalConfirmedUtc = utcNow;
            license.RenewalPendingSinceUtc = null;
            license.ExpiredDateWhenRenewalFiledUtc = null;
            license.RenewalFileNumber =
                renewalApplications.FirstOrDefault()?.UlsFileNumber ?? license.RenewalFileNumber;
            result.RenewalsConfirmed++;
        }

        // The expiration date advancing since the last look IS issuance — whether or not this app
        // ever saw the application pending. Checked first, and independently of RenewalPendingSinceUtc,
        // because requiring a prior "pending" sighting made the state machine misreport any renewal it
        // joined mid-stream: a license renewed between two polls would be recorded as newly *pending*,
        // anchored against its own already-updated expiry, and could then never satisfy the
        // "expiry > anchor" test. It sat on "Renewal pending" until FCC dropped the application and
        // then fell through to plain Active, never once reporting the renewal it had just watched land
        // (found 2026-08-06 on a license granted before it was first observed).
        if (previousExpiry is { } before && license.ExpiredDateUtc is { } after && after > before)
        {
            Confirm();
            return;
        }

        if (license.RenewalPendingSinceUtc is not null)
        {
            // Belt and braces alongside the previousExpiry test above: that one compares against the
            // last poll, this one against the value when the renewal was first seen, which still
            // catches an advance spread across a poll that returned no expiry at all.
            var filedAgainst = license.ExpiredDateWhenRenewalFiledUtc;
            var issued = license.ExpiredDateUtc is { } current &&
                         (filedAgainst is null || current > filedAgainst);

            if (issued)
            {
                Confirm();
                return;
            }

            if (renewal is null)
            {
                license.RenewalPendingSinceUtc = null;
                license.ExpiredDateWhenRenewalFiledUtc = null;

                if (lingeringOnly)
                {
                    // Not an abandonment: this row was re-armed as "pending" by an application FCC
                    // had already granted (the bug this filtering exists to stop). Stand the pending
                    // state down and leave RenewalConfirmedUtc and the file number where they are,
                    // so the row goes back to reporting the renewal it actually got.
                    return;
                }

                // The application vanished without the expiration moving — dismissed, withdrawn, or
                // FCC re-filed it under a new number. Reset to "not pending" so a later application
                // is seen as new; the row simply goes back to reporting its real expiry.
                license.RenewalFileNumber = null;
                result.RenewalsAbandoned++;
                return;
            }

            // Still pending — refresh the file number in case FCC re-issued it, but leave
            // RenewalPendingSinceUtc alone: it records when *we* first saw it, and must not creep
            // forward on every poll.
            license.RenewalFileNumber = renewal.UlsFileNumber ?? license.RenewalFileNumber;
            return;
        }

        if (renewal is not null)
        {
            license.RenewalPendingSinceUtc = utcNow;
            license.RenewalFileNumber = renewal.UlsFileNumber;
            // Anchor on the expiry as it stands right now — the value the renewal must beat. Falls
            // back to the pre-refresh value if this poll returned none, so the anchor is never null
            // when we had something to record.
            license.ExpiredDateWhenRenewalFiledUtc = license.ExpiredDateUtc ?? previousExpiry;
            result.RenewalsDetected++;
        }
    }

    /// <summary>
    /// Whether a renewal application still listed at FCC is one this app has already watched land.
    ///
    /// <para><b>Issuance is terminal, but FCC's pending list does not say so.</b> The application
    /// can sit in <c>pendingApplications</c> for days after the new term is granted, and by then the
    /// expiration has stopped moving — so on the *next* poll the "no advance since last time, and
    /// there's a renewal pending" path read it as a brand-new request. That re-armed the row to
    /// Renewal pending the day after it correctly reported Renewed (observed on KA0MVW,
    /// 2026-08-06/07), and wedged it there permanently: the anchor it recorded was the
    /// already-renewed expiry, a value nothing would ever beat, so the row could only escape when
    /// FCC eventually dropped the application.</para>
    ///
    /// <para>Matched on the file number first, since that identifies the application itself. The
    /// receipt-date fallback covers a response that omits the number: FCC cannot have received a
    /// genuinely new renewal before it issued the last one, and the real ones are ten years
    /// apart.</para>
    /// </summary>
    private static bool IsAlreadyIssued(WatchedLicense license, UlsPendingApplication application)
    {
        if (license.RenewalConfirmedUtc is not { } confirmed) return false;

        if (!string.IsNullOrWhiteSpace(application.UlsFileNumber) &&
            string.Equals(application.UlsFileNumber, license.RenewalFileNumber, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return application.ReceiptDateUtc is { } receipt && receipt <= confirmed;
    }
}

public enum AddWatchedLicenseOutcome
{
    Success,

    /// <summary>Nothing was typed. Distinct from NotFoundAtFcc so the message can ask for input rather than report a failed search.</summary>
    CallSignRequired,

    /// <summary>The ULS mirror could not be reached — say so, rather than telling someone their correct call sign does not exist.</summary>
    LookupUnavailable,

    /// <summary>The lookup worked and FCC has no such record.</summary>
    NotFoundAtFcc,

    /// <summary>Already on this team's watch list. Per team, so another team watching it is not a conflict.</summary>
    AlreadyWatched
}

/// <param name="CallSign">
/// The normalized call sign on success or when already watched; otherwise the caller's own trimmed
/// input, so a "no record for X" message can echo what they actually typed.
/// </param>
/// <param name="License">The created row, on success only — non-null exactly when <see cref="AddWatchedLicenseOutcome.Success"/>.</param>
public sealed record AddWatchedLicenseResult(
    AddWatchedLicenseOutcome Outcome,
    string CallSign,
    WatchedLicense? License = null);

public class LicenseWatchResult
{
    /// <summary>How many rows were due for a refresh this run (capped at <see cref="LicenseWatchService.MaxLookupsPerRun"/>).</summary>
    public int Due { get; set; }

    /// <summary>Rows successfully looked up and updated.</summary>
    public int Checked { get; set; }

    /// <summary>Lookups that could not be performed at all — these keep their stale timestamp and retry next run.</summary>
    public int LookupFailures { get; set; }

    /// <summary>
    /// Rows that threw while being saved — a vanity rename colliding with
    /// <c>IX_WatchedLicenses_TeamId_CallSign</c> being the expected cause. Isolated per row so one
    /// cannot end the sweep (issue #249); surfaced in <see cref="ToString"/> so a run that quietly
    /// skipped a license does not read as a clean run.
    /// </summary>
    public int Failures { get; set; }

    public int NotFound { get; set; }
    public int RenewalsDetected { get; set; }
    public int RenewalsConfirmed { get; set; }
    public int RenewalsAbandoned { get; set; }

    public override string ToString() =>
        $"{Checked}/{Due} checked, {LookupFailures} lookup failure(s), {Failures} save failure(s), {NotFound} not found, " +
        $"renewals: {RenewalsDetected} detected / {RenewalsConfirmed} confirmed / {RenewalsAbandoned} abandoned";
}
