using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// A team's watch list of amateur licenses — expiration dates, and the renewal lifecycle from
/// application through issuance. See docs/renewal-monitor.md.
///
/// <para><b>Named "Renewal Monitor" in the UI, "watched license" in the model.</b> The page is filed
/// under Applicants because a renewal is, technically, an application; the Core types
/// (<see cref="WatchedLicense"/>, <c>LicenseWatchService</c>) keep the mechanical name because that
/// is what they are — a license being watched — and renaming the table would cost a migration on an
/// already-deployed schema for no functional gain.</para>
///
/// <para><b>Open to all four roles, scoped to their own team(s).</b> Unlike VE Roster (admin-only,
/// because it is a contact list plus a per-VE leaderboard), this holds nothing sensitive: call sign,
/// licensee name and expiry are all public FCC record data, and a TeamLead has as much reason to
/// check whether a club member's license is lapsing as anyone else. The <c>[Authorize]</c> here and
/// the nav gate in _AppLayout.cshtml must therefore stay in step — both simply require
/// authentication.</para>
/// </summary>
[Authorize]
public class RenewalMonitorModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    IUlsLookupClient lookupClient,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public bool HasTeamContext { get; private set; }
    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public string TeamSummaryLabel { get; private set; } = "All teams";

    /// <summary>The watch list proper — everything except the rows currently sitting in <see cref="RecentlyRenewed"/>.</summary>
    public IReadOnlyList<WatchedLicenseRow> Licenses { get; private set; } = [];

    /// <summary>
    /// Renewals confirmed inside <see cref="WatchedLicenseStatusExtensions.RenewedHighlightWindow"/>,
    /// shown in their own section below the watch list — the same shape as Applicant Status's
    /// "Recently issued", and for the same reason: a finished outcome is worth seeing once, but it is
    /// not what the working list is for.
    ///
    /// <para><b>Where the two pages part company:</b> a granted candidate leaves Applicant Status for
    /// good, because there is nothing left to watch. A renewed license goes back into the watch list
    /// once the window passes — it is still being watched, just for a term ten years out.</para>
    /// </summary>
    public IReadOnlyList<WatchedLicenseRow> RecentlyRenewed { get; private set; } = [];

    /// <summary>The highlight window, in days, for the section heading. Read from the one definition in Core rather than restated here.</summary>
    public static int RecentlyRenewedWindowDays => WatchedLicenseStatusExtensions.RenewedHighlightWindow.Days;

    /// <summary>Every row on the page, so the per-row remove modals cover both tables.</summary>
    public IEnumerable<WatchedLicenseRow> AllRows => Licenses.Concat(RecentlyRenewed);

    /// <summary>Which team a newly added license is filed under. Only meaningful — and only rendered — when the user can see more than one.</summary>
    [BindProperty]
    public int? AddTeamId { get; set; }

    [BindProperty]
    public string? AddCallSign { get; set; }

    [BindProperty]
    public string? AddNote { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    /// <summary>
    /// Adds a license, resolving the entry against ULS first.
    ///
    /// <para><b>The lookup is synchronous and blocking on purpose.</b> A mistyped call sign that is
    /// merely stored would sit in the list forever showing "not checked yet", and the person who
    /// typed it would be long gone. Resolving now means the error lands while they can still fix it,
    /// and it is also what lets a row entered as an FRN be stored under its call sign — which is what
    /// the list is keyed on and what a human recognises.</para>
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");
        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);

        var entry = AddCallSign?.Trim();
        if (string.IsNullOrWhiteSpace(entry))
        {
            TempData["ErrorMessage"] = "Enter a call sign or FRN.";
            return RedirectToPage(new { TeamId });
        }

        // Re-authorized server-side rather than trusting the posted team id: the picker only decides
        // what is worth rendering.
        var targetTeamId = AddTeamId ?? (AvailableTeams.Count == 1 ? AvailableTeams[0].Id : null);
        if (targetTeamId is null || !AvailableTeams.Any(t => t.Id == targetTeamId))
        {
            TempData["ErrorMessage"] = "Choose a team to add this license to.";
            return RedirectToPage(new { TeamId });
        }

        var lookup = await lookupClient.LookupByFrnAsync(entry, HttpContext.RequestAborted);
        if (lookup is null)
        {
            // The endpoint itself was unreachable. Distinct from "no such call sign" — say so, rather
            // than telling someone their correct call sign does not exist.
            TempData["ErrorMessage"] = $"Couldn't reach the FCC license lookup just now — {entry.ToUpperInvariant()} wasn't added. Try again shortly.";
            return RedirectToPage(new { TeamId });
        }

        if (!lookup.Found || string.IsNullOrWhiteSpace(lookup.CallSign))
        {
            TempData["ErrorMessage"] = $"FCC has no license record for \"{entry}\". Check the call sign or FRN and try again.";
            return RedirectToPage(new { TeamId });
        }

        var callSign = lookup.CallSign.Trim().ToUpperInvariant();
        if (await dbContext.WatchedLicenses.AnyAsync(w => w.TeamId == targetTeamId && w.CallSign == callSign, HttpContext.RequestAborted))
        {
            TempData["ErrorMessage"] = $"{callSign} is already on this team's watch list.";
            return RedirectToPage(new { TeamId });
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var license = new WatchedLicense
        {
            TeamId = targetTeamId.Value,
            CallSign = callSign,
            Note = string.IsNullOrWhiteSpace(AddNote) ? null : AddNote.Trim(),
            AddedByUserId = user.Id,
            AddedUtc = utcNow
        };

        // Populate from the lookup already in hand, so the row is complete on first render instead of
        // showing "not checked yet" until the Worker's next tick. Reuses the service's own mapping so
        // the two can't drift.
        LicenseWatchService.Apply(license, lookup, utcNow, new LicenseWatchResult());

        dbContext.WatchedLicenses.Add(license);
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted);

        // Audited after the save, because EntityId is an int and the row has no id until then.
        dbContext.AddAuditLog(user.Id, "Create", nameof(WatchedLicense), license.Id, $"Added {callSign} to the license watch list", utcNow);
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted);

        TempData["StatusMessage"] = $"{callSign} added to the watch list.";
        return RedirectToPage(new { TeamId });
    }

    public async Task<IActionResult> OnPostRemoveAsync(int watchedLicenseId)
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        var license = await dbContext.WatchedLicenses.FirstOrDefaultAsync(w => w.Id == watchedLicenseId, HttpContext.RequestAborted);
        if (license is null)
        {
            TempData["ErrorMessage"] = "That license is no longer on the watch list.";
            return RedirectToPage(new { TeamId });
        }

        // GetEffectiveTeamIds returns null for a SystemAdmin, meaning "every team" — so a plain
        // Contains check would 403 exactly the person with the most access. Same trap CLAUDE.md
        // records for the unmatched-payment match action.
        var effectiveTeamIds = accessScope.GetEffectiveTeamIds(user);
        if (effectiveTeamIds is not null && !effectiveTeamIds.Contains(license.TeamId))
        {
            return Forbid();
        }

        dbContext.WatchedLicenses.Remove(license);
        dbContext.AddAuditLog(user.Id, "Delete", nameof(WatchedLicense), license.Id, $"Removed {license.CallSign} from the license watch list", timeProvider.GetUtcNow().UtcDateTime);
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted);

        TempData["StatusMessage"] = $"{license.CallSign} removed from the watch list.";
        return RedirectToPage(new { TeamId });
    }

    private async Task LoadAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");

        AvailableTeams = await accessScope.GetAvailableTeamsAsync(dbContext, user);
        AddTeamId ??= TeamId ?? (AvailableTeams.Count == 1 ? AvailableTeams[0].Id : null);

        // null means "every team this user can see, merged" — never "no teams". Using
        // TryResolveViewableTeamId here instead would silently empty the page for a SystemAdmin who
        // hasn't picked a team (the trap CLAUDE.md records for Applicant Status).
        var teamIds = accessScope.ResolveViewableTeamIds(user, TeamId);
        HasTeamContext = teamIds is null || teamIds.Count > 0;
        TeamSummaryLabel = TeamId is not null
            ? AvailableTeams.FirstOrDefault(t => t.Id == TeamId).Name ?? "All teams"
            : "All teams";

        if (!HasTeamContext) return;

        var query = dbContext.WatchedLicenses.Include(w => w.Team).AsQueryable();
        if (teamIds is not null) query = query.Where(w => teamIds.Contains(w.TeamId));

        var rows = await query.ToListAsync(HttpContext.RequestAborted);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        // Status is derived, so the split and the ordering that depends on it both have to happen in
        // memory. The set is one team's watch list — tens of rows, not thousands — so this is not the
        // N+1 shape the report queries have to worry about.
        var all = rows.Select(w => new WatchedLicenseRow(w, w.DeriveStatus(utcNow))).ToList();

        // Membership of both lists is decided by the derived status alone, never by a stored flag or a
        // second date test here: DeriveStatus already owns "is this renewal recent enough to still be
        // worth reporting", and a row leaves this section for the watch list the moment it stops
        // saying Renewed. One rule, so the section and the chip cannot disagree.
        Licenses = [.. all
            .Where(r => r.Status is not WatchedLicenseStatus.Renewed)
            .OrderByDescending(r => r.Status.NeedsAttention())
            .ThenBy(r => r.License.ExpiredDateUtc ?? DateTime.MaxValue)
            .ThenBy(r => r.License.CallSign)];

        RecentlyRenewed = [.. all
            .Where(r => r.Status is WatchedLicenseStatus.Renewed)
            .OrderByDescending(r => r.License.RenewalConfirmedUtc ?? DateTime.MinValue)
            .ThenBy(r => r.License.CallSign)];
    }

    public record WatchedLicenseRow(WatchedLicense License, WatchedLicenseStatus Status)
    {
        public string ExpiresDisplay => License.ExpiredDateUtc?.ToString("MMM d, yyyy") ?? "—";

        /// <summary>Round-trip value for the client-side table sorter — "Apr 2" does not sort against "Mar 30" as text.</summary>
        public string ExpiresSortValue => License.ExpiredDateUtc?.ToString("o") ?? "";

        /// <summary>
        /// When the renewal was confirmed, for the Recently renewed section. Carries the year, unlike
        /// the compact "Issued MMM d" in <see cref="RenewalDisplay"/> — this is the column a reader is
        /// looking at directly rather than a note beside a status chip.
        /// </summary>
        public string RenewedDisplay => License.RenewalConfirmedUtc?.ToString("MMM d, yyyy") ?? "—";

        public string RenewedSortValue => License.RenewalConfirmedUtc?.ToString("o") ?? "";

        /// <summary>Delegates to the shared definition in WatchedLicenseStatusExtensions — a second copy here is exactly how the pill and the status chip would come to disagree.</summary>
        public int? DaysUntilExpiry(DateTime utcNow) => License.DaysUntilExpiry(utcNow);

        /// <summary>
        /// Keyed off the derived status rather than testing the two dates in its own order — the
        /// chip saying Renewed while this column says "Filed" is precisely the disagreement that
        /// made a lingering FCC application look like a fresh one.
        /// </summary>
        public string RenewalDisplay => Status is WatchedLicenseStatus.Renewed
            ? License.RenewalConfirmedUtc is { } issued ? $"Issued {issued:MMM d}" : "Issued"
            : License.RenewalPendingSinceUtc is { } since
                ? $"Filed, seen {since:MMM d}"
                : License.RenewalConfirmedUtc is { } confirmed
                    ? $"Issued {confirmed:MMM d}"
                    : "—";
    }
}
