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

        foreach (var volunteerExaminer in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookup = await lookupClient.LookupByFrnAsync(volunteerExaminer.CallSign!, cancellationToken);
            if (lookup is null)
            {
                // The lookup itself failed, so nothing was learned. Deliberately does not stamp
                // LicenseLastCheckedUtc — leaving the row stale is what makes the next run retry it.
                result.LookupFailures++;
                continue;
            }

            Apply(volunteerExaminer, lookup, utcNow, result);

            // Per row, same reasoning as every other scan-based service here: a crash mid-run keeps
            // whatever it already did.
            await dbContext.SaveChangesAsync(cancellationToken);
            result.Checked++;
        }

        logger.LogInformation("VE license watch finished: {Result}", result);
        return result;
    }

    /// <summary>
    /// Copies the ULS record onto the VE. Public and static so the tests can drive it directly
    /// against a fabricated <see cref="UlsLookupResult"/>, matching LicenseWatchService.Apply.
    /// </summary>
    public static void Apply(VolunteerExaminer volunteerExaminer, UlsLookupResult lookup, DateTime utcNow, VeLicenseWatchResult result)
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
            volunteerExaminer.Frn = lookup.Frn;
            result.FrnsBackfilled++;
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
    public int NotFound { get; set; }
    public int FrnsBackfilled { get; set; }
    public int CallSignsChanged { get; set; }

    public override string ToString() =>
        $"{Checked}/{Due} checked, {Skipped} skipped (no usable call sign), {LookupFailures} lookup failure(s), " +
        $"{NotFound} not found, {FrnsBackfilled} FRN(s) backfilled, {CallSignsChanged} call sign change(s)";
}
