using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Uls;

/// <summary>
/// Refreshes the VE roster's own licenses from ExamTools' ULS mirror (issue #107) — the other half
/// of the license work, alongside <see cref="LicenseWatchService"/>'s hand-curated watch list.
///
/// <para><b>Why a second service rather than rows in the Renewal Monitor.</b> That list's premise is
/// that a human deliberately added each entry; filling it with thirty VEs nobody added would break
/// it. The VE roster is populated automatically and asks a different question — not "is this
/// lapsing?" but "can this person legally serve at Saturday's session?" — so the cached fields live
/// on <see cref="VolunteerExaminer"/> and this sweep populates them. The status *rules* are shared
/// via <see cref="ILicenseSnapshot"/>, which is the part that actually had to be.</para>
///
/// <para><b>Deliberately not part of VolunteerExaminerSyncService.</b> That service reconciles roster
/// membership from ExamTools and has a hard-won bound on which sessions it touches (see
/// docs/historical-import.md); bolting FCC lookups onto it would entangle two unrelated cadences and
/// risk undoing that bound. Roster membership and license state are separate concerns that happen to
/// share an entity.</para>
/// </summary>
public class VolunteerExaminerLicenseWatchService(
    AppDbContext dbContext,
    IUlsLookupClient lookupClient,
    TimeProvider timeProvider,
    ILogger<VolunteerExaminerLicenseWatchService> logger)
{
    /// <summary>Same six hours as the watch list: short enough that the daily anchored run always finds every row due, long enough that a second run the same day is not a second full sweep.</summary>
    public static readonly TimeSpan RefreshInterval = LicenseWatchService.RefreshInterval;

    /// <summary>Ceiling per run, so a large roster cannot turn one run into a burst against a third party's mirror. The remainder are picked up next time, least-recently-checked first.</summary>
    public const int MaxLookupsPerRun = 250;

    public async Task<VeLicenseWatchResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = new VeLicenseWatchResult();
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var staleBefore = utcNow - RefreshInterval;

        // Two filters that both matter:
        //
        //   Active membership — a VE retired from every team they served is not going to be assigned
        //   to a session, so their license is nobody's question. Without this the sweep would grow
        //   forever as teams turn over, spending calls on people who will never appear again.
        //
        //   CallSign != null — the cheap half of "is there anything to look up". The real test is
        //   CallSign.IsUsable, which cannot translate to SQL, so it is applied in memory below;
        //   ExamTools' literal "<UNKNOWN>" would otherwise be looked up forever and come back
        //   not-found every time.
        var candidates = await dbContext.VolunteerExaminers
            .Where(v => v.CallSign != null
                        && v.TeamMemberships.Any(m => m.IsActive)
                        && (v.LicenseLastCheckedUtc == null || v.LicenseLastCheckedUtc < staleBefore))
            .OrderBy(v => v.LicenseLastCheckedUtc)
            .Take(MaxLookupsPerRun * 2)
            .ToListAsync(cancellationToken);

        var due = candidates.Where(v => CallSign.IsUsable(v.CallSign)).Take(MaxLookupsPerRun).ToList();
        result.Skipped = candidates.Count - due.Count;
        result.Due = due.Count;

        // Every FRN already spoken for, so the backfill below can tell "this is my FRN" from "this
        // FRN belongs to somebody else". Loaded once rather than queried per row.
        var frnOwners = await dbContext.VolunteerExaminers
            .Where(v => v.Frn != null)
            .ToDictionaryAsync(v => v.Frn!, v => v.Id, cancellationToken);

        foreach (var volunteerExaminer in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per row, and isolated per row. Without the try/catch a single bad row aborts the whole
            // sweep and every later VE goes unchecked — which is exactly what happened on the first
            // real run, when an FRN collision threw out of SaveChangesAsync. Same shape as
            // VolunteerExaminerSyncService's per-session guard.
            try
            {
                var lookup = await lookupClient.LookupByFrnAsync(volunteerExaminer.CallSign!, cancellationToken);
                if (lookup is null)
                {
                    // The lookup itself failed, so nothing was learned. Deliberately does not stamp
                    // LicenseLastCheckedUtc — leaving the row stale is what makes the next run retry it.
                    result.LookupFailures++;
                    continue;
                }

                Apply(volunteerExaminer, lookup, utcNow, frnOwners, result);

                await dbContext.SaveChangesAsync(cancellationToken);
                result.Checked++;
            }
            catch (Exception ex)
            {
                // Discard the poisoned tracked state, or every later SaveChangesAsync in this run
                // retries the same failing entity — the trap JobRunHistoryLogger already documents.
                //
                // **Scoped to this row. It was ChangeTracker.Clear() until 2026-08-11 (issue #231),
                // and Clear() detaches EVERYTHING** — including the rest of `due`, which was loaded
                // tracked before the loop. One FRN collision on VE #7 of 250 therefore left VEs
                // #8-250 being mutated while detached: LicenseLastCheckedUtc, Frn, LicenseStatus and
                // the added history rows all went nowhere, SaveChangesAsync wrote nothing, and
                // result.Checked++ still ran. The job reported "checked 243", Job History rendered
                // green, and because the stamp never persisted the next run did it all again.
                DetachFailedRow(volunteerExaminer);
                result.Failures++;
                logger.LogError(ex, "Failed to refresh license for VE {VolunteerExaminerId} ({CallSign})",
                    volunteerExaminer.Id, volunteerExaminer.CallSign);
            }
        }

        foreach (var (veId, ownerId, frn) in result.ConflictingFrnOwnerIds)
        {
            logger.LogWarning(
                "VE {VolunteerExaminerId} and VE {ExistingOwnerId} both resolve to FRN {Frn} — FCC says they are the same person. " +
                "Left as two records; a human must decide whether to merge them",
                veId, ownerId, frn);
        }

        logger.LogInformation("VE license watch finished: {Result}", result);
        return result;
    }

    /// <summary>
    /// Detaches one failed row's pending state and nothing else, so the rest of the sweep keeps its
    /// own changes (issue #231 — see the catch block that calls this).
    ///
    /// <para><see cref="Apply"/> touches exactly two things: the <see cref="VolunteerExaminer"/>
    /// itself, and any <see cref="VeCallSignHistory"/> row it adds for a rename. Those history rows
    /// are constructed with a real <c>VolunteerExaminerId</c> (the VE is already persisted), so they
    /// can be matched directly rather than through navigation fixup the throw may have
    /// interrupted.</para>
    /// </summary>
    private void DetachFailedRow(VolunteerExaminer volunteerExaminer)
    {
        var pendingHistory = dbContext.ChangeTracker.Entries<VeCallSignHistory>()
            .Where(e => e.State != EntityState.Unchanged
                        && e.Entity.VolunteerExaminerId == volunteerExaminer.Id)
            .ToList();

        foreach (var entry in pendingHistory)
        {
            entry.State = EntityState.Detached;
        }

        dbContext.Entry(volunteerExaminer).State = EntityState.Detached;
    }

    /// <summary>
    /// Copies the ULS record onto the VE. Public and static so the tests can drive it directly
    /// against a fabricated <see cref="UlsLookupResult"/>, matching LicenseWatchService.Apply.
    /// </summary>
    public static void Apply(
        VolunteerExaminer volunteerExaminer, UlsLookupResult lookup, DateTime utcNow,
        Dictionary<string, int> frnOwners, VeLicenseWatchResult result)
    {
        volunteerExaminer.LicenseLastCheckedUtc = utcNow;

        if (!lookup.Found)
        {
            volunteerExaminer.LicenseNotFoundAtFcc = true;
            result.NotFound++;
            return;
        }

        volunteerExaminer.LicenseNotFoundAtFcc = false;
        volunteerExaminer.LicenseStatus = lookup.LicenseStatus;
        volunteerExaminer.OperatorClass = lookup.OperatorClass;
        volunteerExaminer.LicenseGrantDateUtc = lookup.GrantDateUtc;
        volunteerExaminer.LicenseExpiresUtc = lookup.ExpiredDateUtc;
        volunteerExaminer.LicenseCancellationDateUtc = lookup.CancellationDateUtc;

        // **This is the point of the whole sweep for issue #142's identity model.** ExamTools' roster
        // never reports an FRN, so until now every VE had none — and FRN is the only identifier that
        // survives a vanity call sign change. Backfilling it here is what eventually makes matching
        // robust rather than call-sign-dependent.
        if (!string.IsNullOrWhiteSpace(lookup.Frn) && volunteerExaminer.Frn != lookup.Frn)
        {
            // **A collision here is a discovery, not an error.** FRN is unique per person, so two VE
            // rows resolving to one FRN is *proof* they are the same human — stronger evidence than
            // a shared call sign, which can be a reissue. It happens for the rows the #142 merge
            // deliberately left alone because their names disagreed, and for a person whose old and
            // new call signs both exist as separate rows.
            //
            // Recorded and skipped rather than written: taking the FRN would violate the unique
            // index, and stealing it from the other row would be worse — merging two people is not
            // reversible and is a human's decision. The pair is named in the log, and the count
            // reaches the ops dashboard through the job result.
            if (frnOwners.TryGetValue(lookup.Frn, out var ownerId) && ownerId != volunteerExaminer.Id)
            {
                // Stored, not just logged. This is the strongest evidence the app will ever have that
                // two records are one person, and leaving it in a log line meant the merge screen —
                // the one place it matters — could only see a shared call sign and had to call proven
                // duplicates "needs checking".
                volunteerExaminer.ConflictingFrn = lookup.Frn;
                result.FrnConflicts++;
                result.ConflictingFrnOwnerIds.Add((volunteerExaminer.Id, ownerId, lookup.Frn));
            }
            else
            {
                volunteerExaminer.Frn = lookup.Frn;
                volunteerExaminer.ConflictingFrn = null;
                frnOwners[lookup.Frn] = volunteerExaminer.Id;
                result.FrnsBackfilled++;
            }
        }

        // A call sign change: follow FCC, and keep the old one so a roster still naming them by it
        // resolves to this person instead of minting a second.
        var reported = CallSign.Normalize(lookup.CallSign);
        if (reported is not null && !string.Equals(reported, volunteerExaminer.CallSign, StringComparison.OrdinalIgnoreCase))
        {
            volunteerExaminer.CallSignHistory.Add(new VeCallSignHistory
            {
                VolunteerExaminerId = volunteerExaminer.Id,
                CallSign = volunteerExaminer.CallSign!,
                FirstSeenUtc = volunteerExaminer.CreatedUtc,
                ReplacedUtc = utcNow
            });
            volunteerExaminer.CallSign = reported;
            result.CallSignsChanged++;
        }
    }
}

public class VeLicenseWatchResult
{
    public int Due { get; set; }
    public int Checked { get; set; }

    /// <summary>Rows that were stale but have nothing to look up — no usable call sign. Counted rather than silently dropped, so a roster full of them is visible on the ops dashboard.</summary>
    public int Skipped { get; set; }

    public int LookupFailures { get; set; }

    /// <summary>Rows that threw while being saved. Isolated per row so one cannot end the sweep — see RunAsync.</summary>
    public int Failures { get; set; }

    public int NotFound { get; set; }
    public int FrnsBackfilled { get; set; }
    public int CallSignsChanged { get; set; }

    /// <summary>
    /// Two VE rows that FCC says are one person. Surfaced rather than resolved: merging people is
    /// not reversible, so a human decides.
    /// </summary>
    public int FrnConflicts { get; set; }

    /// <summary>(this row, the row already holding that FRN, the FRN) — logged so the pair can actually be found.</summary>
    public List<(int VolunteerExaminerId, int ExistingOwnerId, string Frn)> ConflictingFrnOwnerIds { get; } = [];

    public override string ToString() =>
        $"{Checked}/{Due} checked, {Skipped} skipped (no usable call sign), {LookupFailures} lookup failure(s), " +
        $"{Failures} error(s), {NotFound} not found, {FrnsBackfilled} FRN(s) backfilled, " +
        $"{CallSignsChanged} call sign change(s), {FrnConflicts} FRN conflict(s)";
}
