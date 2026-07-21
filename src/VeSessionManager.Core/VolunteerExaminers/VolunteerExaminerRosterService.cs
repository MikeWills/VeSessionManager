using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Phase 9b's "VE roster editing" — the manual add/remove-VE-chip actions on a session's detail
/// page. Matches/creates by (TeamId, CallSign) uppercased, the same convention
/// VolunteerExaminerSyncService uses for its own ExamTools-driven reconciliation.
///
/// Known tension with that sync service, worth calling out rather than silently discovering later:
/// VolunteerExaminerSyncService *fully reconciles* each active session's roster against ExamTools
/// every poll — so a manual removal here will be re-added (and a manual addition removed) on the
/// very next poll if ExamTools' own roster for that session still/never reports that VE. That's
/// intentional, not a bug to fix in this phase: this action is for correcting the roster when
/// ExamTools is wrong or lagging for a specific session, same spirit as any other manual override
/// in this app, but it isn't "sticky" against a source of truth that disagrees.
/// </summary>
public class VolunteerExaminerRosterService(AppDbContext dbContext, TimeProvider timeProvider, ILogger<VolunteerExaminerRosterService> logger)
{
    public async Task<VeRosterActionResult> AddAsync(int sessionId, string callSign, string? name, int userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .Include(s => s.SessionVolunteerExaminers)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return VeRosterActionResult.SessionNotFound;
        }

        var normalizedCallSign = callSign.Trim().ToUpperInvariant();
        var volunteerExaminer = await dbContext.VolunteerExaminers
            .FirstOrDefaultAsync(v => v.TeamId == session.TeamId && v.CallSign == normalizedCallSign, cancellationToken);

        if (volunteerExaminer is null)
        {
            volunteerExaminer = new VolunteerExaminer
            {
                Name = string.IsNullOrWhiteSpace(name) ? normalizedCallSign : name.Trim(),
                CallSign = normalizedCallSign,
                TeamId = session.TeamId
            };
            dbContext.VolunteerExaminers.Add(volunteerExaminer);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (session.SessionVolunteerExaminers.Any(l => l.VolunteerExaminerId == volunteerExaminer.Id))
        {
            return VeRosterActionResult.AlreadyOnRoster;
        }

        dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer { SessionId = session.Id, VolunteerExaminerId = volunteerExaminer.Id });

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "VeAddedToSessionRoster",
            EntityType = nameof(Session),
            EntityId = session.Id,
            TimestampUtc = now,
            Details = $"VE {normalizedCallSign} added to session {session.ExamToolsSessionId} roster."
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("VE {CallSign} added to session {SessionId} roster by user {UserId}", normalizedCallSign, session.Id, userId);
        return VeRosterActionResult.Success;
    }

    public async Task<VeRosterActionResult> RemoveAsync(int sessionId, int volunteerExaminerId, int userId, CancellationToken cancellationToken)
    {
        var link = await dbContext.SessionVolunteerExaminers
            .Include(l => l.VolunteerExaminer)
            .Include(l => l.Session)
            .FirstOrDefaultAsync(l => l.SessionId == sessionId && l.VolunteerExaminerId == volunteerExaminerId, cancellationToken);
        if (link is null)
        {
            return VeRosterActionResult.NotOnRoster;
        }

        dbContext.SessionVolunteerExaminers.Remove(link);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "VeRemovedFromSessionRoster",
            EntityType = nameof(Session),
            EntityId = sessionId,
            TimestampUtc = now,
            Details = $"VE {link.VolunteerExaminer.CallSign} removed from session {link.Session.ExamToolsSessionId} roster."
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("VE {VolunteerExaminerId} removed from session {SessionId} roster by user {UserId}", volunteerExaminerId, sessionId, userId);
        return VeRosterActionResult.Success;
    }
}

public enum VeRosterActionResult
{
    Success,
    SessionNotFound,
    AlreadyOnRoster,
    NotOnRoster
}
