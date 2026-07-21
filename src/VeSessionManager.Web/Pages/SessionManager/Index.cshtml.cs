using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
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
/// for session visibility — see docs/admin-auth.md's role hierarchy.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager")]
public class IndexModel(AppDbContext dbContext, UserManager<User> userManager, SessionAccessScope accessScope, TimeProvider timeProvider) : PageModel
{
    public IReadOnlyList<SessionRow> Sessions { get; private set; } = [];
    public string ActiveFilter { get; private set; } = "Upcoming";

    public async Task OnGetAsync(string? filter)
    {
        ActiveFilter = filter is "NeedsReview" or "Past" or "All" ? filter : "Upcoming";

        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");
        var now = timeProvider.GetUtcNow().UtcDateTime;

        IQueryable<Session> query = accessScope.Scope(dbContext.Sessions, user)
            .Include(s => s.Vec)
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
        int CandidateCount,
        bool RescheduleFlagged,
        string StatusChipClass,
        string StatusChipLabel,
        string VecSubmissionChipClass,
        string VecSubmissionChipLabel);
}
