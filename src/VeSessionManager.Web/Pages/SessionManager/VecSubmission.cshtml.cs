using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VecSubmissions;

namespace VeSessionManager.Web.Pages.SessionManager;

/// <summary>
/// Phase 9b's "VEC Submission" nav destination — Phase 8's "dashboard indicator: count of sessions
/// pending VEC submission" (VecSubmissionReportService), plus a list of every non-cancelled session
/// with an inline "Mark submitted" action reusing the same VecSubmissionService.MarkSubmittedAsync
/// the session-detail page's toggle uses. Not one of the design handoff's four mocked screens —
/// styled with the same design-system table/chip components as everything else. TeamLead was added
/// in the TeamLead-read-only-view fix (see TODO.md) — the listing is already scoped correctly for
/// TeamLead via GetEffectiveTeamId, but the inline "Mark submitted" action is gated behind CanEdit
/// so a TeamLead sees the status without a button that would 403.
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin,SessionManager,TeamLead")]
public class VecSubmissionModel(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SessionAccessScope accessScope,
    VecSubmissionReportService reportService,
    VecSubmissionService submissionService) : PageModel
{
    public bool HasTeamContext { get; private set; }
    public bool CanEdit { get; private set; }
    public int PendingCount { get; private set; }
    public IReadOnlyList<SessionRow> Sessions { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var user = await userManager.GetUserWithManagerAsync(dbContext, User) ?? throw new InvalidOperationException("No authenticated user for an [Authorize]d page.");
        CanEdit = user.Role != UserRole.TeamLead;
        var teamId = accessScope.GetEffectiveTeamId(user);
        HasTeamContext = teamId is not null;
        if (teamId is not int id)
        {
            return;
        }

        PendingCount = await reportService.GetPendingSubmissionCountAsync(id, CancellationToken.None);

        var sessions = await dbContext.Sessions
            .Include(s => s.Vec)
            .Where(s => s.TeamId == id && s.Status == SessionStatus.Active)
            .OrderByDescending(s => s.ScheduledStartUtc)
            .ToListAsync();

        Sessions = sessions.Select(s => new SessionRow(
            s.Id,
            s.ExamToolsSessionId,
            s.ScheduledStartUtc.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            s.Vec.Name,
            s.VecSubmissionStatus == VecSubmissionStatus.Submitted,
            s.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "chip-green" : "chip-neutral",
            s.VecSubmissionStatus == VecSubmissionStatus.Submitted ? "Submitted" : "Not submitted")).ToList();
    }

    public async Task<IActionResult> OnPostMarkSubmittedAsync(int sessionId)
    {
        var user = await userManager.GetUserAsync(User);
        var session = user is null ? null : await dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (user is null || session is null || !accessScope.CanEdit(user, session))
        {
            return Forbid();
        }

        var result = await submissionService.MarkSubmittedAsync(sessionId, user.Id, CancellationToken.None);
        TempData[result == VecSubmissionMarkResult.Marked ? "StatusMessage" : "ErrorMessage"] =
            result == VecSubmissionMarkResult.Marked ? "Session marked submitted to VEC." : "Session is already marked submitted.";
        return RedirectToPage();
    }

    public record SessionRow(int Id, string ExamToolsSessionId, string DateLine, string VecName, bool Submitted, string ChipClass, string ChipLabel);
}
