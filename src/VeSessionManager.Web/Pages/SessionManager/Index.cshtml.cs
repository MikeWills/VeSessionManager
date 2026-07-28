using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Phase 9b's real session list, replacing the Phase 9a placeholder — recreated from
/// design_handoff_vesessionmanager_admin_ui/session-list.html. TeamAdmin is included alongside
/// SessionManager (not just SystemAdmin/SessionManager, the original 9a placeholder's attribute)
/// because SessionAccessScope already treats TeamAdmin as an equal-scope superset of SessionManager
/// for session visibility — see docs/admin-auth.md's role hierarchy. TeamLead was added in the
/// TeamLead-read-only-view fix (see TODO.md) — SessionAccessScope.Scope already resolves a
/// TeamLead's effective teams the same way as everyone else, this page just needed the role added.
///
/// Multi-team (issue #19) + team filter/column (issue #17): a user belonging to more than one team
/// sees every team's sessions mixed together by default, with a Team column to tell them apart and
/// a filter-pill row (TeamId/AvailableTeams) to narrow down to just one.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]
public class IndexModel(AppDbContext dbContext, UserManager<User> userManager, SessionAccessScope accessScope, TimeProvider timeProvider) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int? TeamId { get; set; }

    public IReadOnlyList<(int Id, string Name)> AvailableTeams { get; private set; } = [];
    public IReadOnlyList<SessionRow> Sessions { get; private set; } = [];
    public string ActiveFilter { get; private set; } = "Upcoming";

    public async Task OnGetAsync(string? filter)
    {
        ActiveFilter = filter is "NeedsReview" or "Past" or "All" ? filter : "Upcoming";

        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");
        var now = timeProvider.GetUtcNow().UtcDateTime;

        AvailableTeams = user.Role == UserRole.SystemAdmin
            ? await dbContext.Teams.OrderBy(t => t.Name).Select(t => new ValueTuple<int, string>(t.Id, t.Name)).ToListAsync()
            : (accessScope.GetEffectiveTeamIds(user) ?? [])
                .Join(await dbContext.Teams.ToListAsync(), id => id, t => t.Id, (_, t) => new ValueTuple<int, string>(t.Id, t.Name))
                .OrderBy(t => t.Item2).ToList();

        IQueryable<Session> query = accessScope.Scope(dbContext.Sessions, user, TeamId)
            .Include(s => s.Vec)
            .Include(s => s.Team)
            .Include(s => s.Candidates);

        query = ActiveFilter switch
        {
            "NeedsReview" => query.Where(s => s.RescheduleFlaggedForReview),
            "Past" => query.Where(s => s.ScheduledStartUtc < now),
            "All" => query,
            _ => query.Where(s => s.Status == SessionStatus.Active && s.ScheduledStartUtc >= now)
        };

        var sessions = await query.OrderBy(s => s.ScheduledStartUtc).ToListAsync();
        Sessions = sessions.Select(ToRow).ToList();
    }

    private static SessionRow ToRow(Session s)
    {
        var subParts = new List<string> { s.ExamToolsSessionId };
        if (s.ZoomMeetingId is not null)
        {
            subParts.Add("Zoom");
        }
        if (s.TestingCompletedUtc is not null)
        {
            subParts.Add("Completed");
        }
        if (s.Status == SessionStatus.Cancelled)
        {
            subParts.Add("Cancelled");
        }

        var (statusClass, statusLabel) = s.Status == SessionStatus.Cancelled ? ("chip-brick", "Cancelled")
            : s.RescheduleFlaggedForReview ? ("chip-amber", "Reschedule flagged")
            : s.TestingCompletedUtc is not null ? ("chip-neutral", "Completed")
            : ("chip-green", "Active");

        var (vecClass, vecLabel) = s.Status == SessionStatus.Cancelled ? ("chip-neutral", "—")
            : s.VecSubmissionStatus == VecSubmissionStatus.Submitted ? ("chip-green", "Submitted")
            : ("chip-neutral", "Not submitted");

        return new SessionRow(
            s.Id,
            s.ScheduledStartUtc.ToString("ddd, MMM d · h:mm tt", CultureInfo.InvariantCulture),
            string.Join(" · ", subParts),
            s.Vec.Name,
            s.Team.Name,
            s.Candidates.Count,
            s.RescheduleFlaggedForReview,
            statusClass, statusLabel,
            vecClass, vecLabel);
    }

    public record SessionRow(
        int Id,
        string TitleLine,
        string SubLine,
        string VecName,
        string TeamName,
        int CandidateCount,
        bool RescheduleFlagged,
        string StatusChipClass,
        string StatusChipLabel,
        string VecSubmissionChipClass,
        string VecSubmissionChipLabel);
}
