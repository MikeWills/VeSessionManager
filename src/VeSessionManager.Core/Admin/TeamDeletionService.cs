using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VecSubmissions;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Deletes a team and everything it owns, permanently (Mike, 2026-08-21: <i>"delete which would
/// delete everything related to that team, sessions, VEs, history, everything"</i>).
///
/// <para><b>Deactivating is the usual answer</b> — see <see cref="Team.DeactivatedUtc"/>, which stops
/// the app acting for a team while keeping its history readable. This is for the other case: a team
/// that should never have existed, or whose data is genuinely unwanted. It cannot be undone.</para>
///
/// <para><b>Order is the whole difficulty.</b> Thirteen of a team's child tables are
/// <c>Restrict</c>, so nothing is removed for us — each has to go explicitly, leaves first, or
/// <c>SaveChangesAsync</c> throws. The order below is derived from the model, and
/// <c>TeamDeletionCoverageTests</c> fails if a new team-scoped table is added without being taught
/// to this method: a missed table means either a hard failure or, worse, rows left pointing at a
/// team id that no longer exists.</para>
///
/// <para><b>What survives, and why.</b> <see cref="Vec"/> and <see cref="FeeConfiguration"/> are
/// <i>parents</i> of a team, not children — the hierarchy is VEC ⇒ Team ⇒ VE (docs/multi-team.md) —
/// and user accounts are people. Square keeps its own record of every payment and refund; ARRL keeps
/// whatever was filed with them. This deletes what this app holds, which is all it can.</para>
/// </summary>
public class TeamDeletionService(
    AppDbContext dbContext,
    ArrlSubmissionArchiveStore archiveStore,
    TimeProvider timeProvider,
    ILogger<TeamDeletionService> logger)
{
    /// <param name="VolunteerExaminersDeleted">
    /// VEs for whom this was the only team. A VE examining for two clubs is a person, not team
    /// property, and keeps existing — losing only the membership.
    /// </param>
    /// <param name="ArchiveFilesDeleted">
    /// ARRL submission archives removed from disk. Counted separately because they are the one part
    /// of this that no transaction can roll back.
    /// </param>
    public record TeamDeletionSummary(
        int Sessions,
        int Candidates,
        int Payments,
        int Messages,
        int VolunteerExaminersDeleted,
        int MembershipsRemoved,
        int ArchiveFilesDeleted,
        int AuditEntriesRemoved);

    /// <summary>
    /// What deleting this team would take with it, for the confirmation screen. Read-only.
    ///
    /// <para>Counted rather than described in general terms on purpose: "this will delete 47
    /// candidates" is a fact somebody can check against what they expect, and is the last chance to
    /// notice the wrong team is selected.</para>
    /// </summary>
    public async Task<TeamDeletionSummary?> PreviewAsync(int teamId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Teams.AnyAsync(t => t.Id == teamId, cancellationToken))
        {
            return null;
        }

        var sessionIds = await dbContext.Sessions.Where(s => s.TeamId == teamId).Select(s => s.Id).ToListAsync(cancellationToken);
        var candidateIds = await dbContext.Candidates.Where(c => sessionIds.Contains(c.SessionId)).Select(c => c.Id).ToListAsync(cancellationToken);

        return new TeamDeletionSummary(
            Sessions: sessionIds.Count,
            Candidates: candidateIds.Count,
            Payments: await dbContext.Payments.CountAsync(p => candidateIds.Contains(p.CandidateId), cancellationToken),
            Messages: await dbContext.MessageRules.CountAsync(m => m.TeamId == teamId, cancellationToken),
            VolunteerExaminersDeleted: (await FindVesToDeleteAsync(teamId, cancellationToken)).Count,
            MembershipsRemoved: await dbContext.VeTeamMemberships.CountAsync(m => m.TeamId == teamId, cancellationToken),
            ArchiveFilesDeleted: await dbContext.ArrlVecSubmissions.CountAsync(a => a.TeamId == teamId, cancellationToken),
            AuditEntriesRemoved: await dbContext.AuditLogs.CountAsync(a => a.TeamId == teamId, cancellationToken));
    }

    public async Task<(TeamActionResult Result, TeamDeletionSummary? Summary)> DeleteAsync(
        int teamId, int userId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return (TeamActionResult.NotFound, null);
        }

        var summary = await PreviewAsync(teamId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var teamName = team.Name;

        // ⚠️ Files first, and outside any transaction, because nothing rolls them back. A file left
        // behind after the row naming it is gone is unreachable forever — nothing knows its path. A
        // file deleted before a save that then fails is the same team, still deletable, missing an
        // archive it was about to lose anyway. The two failure modes are not symmetric.
        var archiveFilesDeleted = await DeleteArchiveFilesAsync(teamId, cancellationToken);

        var sessionIds = await dbContext.Sessions.Where(s => s.TeamId == teamId).Select(s => s.Id).ToListAsync(cancellationToken);
        var candidateIds = await dbContext.Candidates.Where(c => sessionIds.Contains(c.SessionId)).Select(c => c.Id).ToListAsync(cancellationToken);
        var paymentIds = await dbContext.Payments.Where(p => candidateIds.Contains(p.CandidateId)).Select(p => p.Id).ToListAsync(cancellationToken);
        var vesToDelete = await FindVesToDeleteAsync(teamId, cancellationToken);

        // Written before the rows go, exactly as SessionActionService does: EntityId is a plain int
        // column with no foreign key, so it stays a valid forensic record once the team is gone.
        //
        // ⚠️ Deliberately carries NO teamId. Attributed to the team it describes, it would be caught
        // by the sweep two dozen lines below that clears this team's audit rows — the record of the
        // deletion would delete itself, which is the one entry that must survive.
        dbContext.AddAuditLog(userId, "TeamDeleted", nameof(Team), teamId,
            $"Team {teamId} ({teamName}) permanently deleted, along with {summary!.Sessions} session(s), "
            + $"{summary.Candidates} candidate(s), {summary.Payments} payment(s), {summary.Messages} message(s), "
            + $"{summary.MembershipsRemoved} VE membership(s) ({summary.VolunteerExaminersDeleted} VE record(s) removed outright), "
            + $"{archiveFilesDeleted} ARRL archive file(s), and {summary.AuditEntriesRemoved} audit entry(s). "
            + "Square and ARRL keep their own records; this removed only what this app held.",
            now);

        // Leaves first. Every list below is scoped to this team's own rows — never a bare
        // "delete all candidates" — which is what keeps a second team on the same deployment intact.
        await RemoveAsync(dbContext.Refunds.Where(r => r.TeamId == teamId || (r.PaymentId != null && paymentIds.Contains(r.PaymentId.Value))), cancellationToken);
        await RemoveAsync(dbContext.UnmatchedSquarePayments.Where(u => u.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.CandidateEmailSends.Where(x => candidateIds.Contains(x.CandidateId)), cancellationToken);
        await RemoveAsync(dbContext.CandidateUlsHistoryEntries.Where(x => candidateIds.Contains(x.CandidateId)), cancellationToken);
        await RemoveAsync(dbContext.Payments.Where(p => candidateIds.Contains(p.CandidateId)), cancellationToken);
        await RemoveAsync(dbContext.Candidates.Where(c => candidateIds.Contains(c.Id)), cancellationToken);
        await RemoveAsync(dbContext.SessionVolunteerExaminers.Where(x => sessionIds.Contains(x.SessionId)), cancellationToken);
        await RemoveAsync(dbContext.ArrlVecSubmissions.Where(a => a.TeamId == teamId || (a.SessionId != null && sessionIds.Contains(a.SessionId.Value))), cancellationToken);
        await RemoveAsync(dbContext.Sessions.Where(s => s.TeamId == teamId), cancellationToken);

        await RemoveAsync(dbContext.MessageRuleRuns.Where(r => r.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.MessageRules.Where(m => m.TeamId == teamId), cancellationToken);

        await RemoveAsync(dbContext.VeTagAssignments.Where(a => a.VeTeamMembership!.TeamId == teamId || a.VeTag!.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.VeTags.Where(t => t.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.VeTeamMemberships.Where(m => m.TeamId == teamId), cancellationToken);

        await RemoveAsync(dbContext.EmailSettings.Where(e => e.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.WatchedLicenses.Where(w => w.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.HistoricalImportRequests.Where(h => h.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.JobRunHistories.Where(j => j.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.ReconciliationFindings.Where(f => f.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.SkippedSessions.Where(s => s.TeamId == teamId), cancellationToken);
        await RemoveAsync(dbContext.UserTeams.Where(u => u.TeamId == teamId), cancellationToken);

        // The VEs whose only home this was. Their own children (call-sign history, tokens, email
        // change requests, accreditations, tag assignments) are all Cascade, so they follow.
        dbContext.VolunteerExaminers.RemoveRange(vesToDelete);

        // ⚠️ Mike's ruling: "Log the team delete, but delete the audit logs." Only rows this team is
        // ATTRIBUTED to are identifiable — AuditLog.TeamId is populated on background-job writes and
        // left null on user-attributed ones, which scope through the acting user's memberships
        // instead. Those stay, describing entities that no longer exist. Said plainly rather than
        // papered over: a fuller sweep would have to guess from EntityType/EntityId and would
        // eventually delete another team's history by collision.
        await RemoveAsync(dbContext.AuditLogs.Where(a => a.TeamId == teamId), cancellationToken);

        dbContext.Teams.Remove(team);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Team {TeamId} ({TeamName}) permanently deleted by user {UserId} — {SessionCount} session(s), {CandidateCount} candidate(s), {PaymentCount} payment(s), {MessageCount} message(s), {VeCount} VE record(s), {ArchiveCount} archive file(s)",
            teamId, teamName, userId, summary.Sessions, summary.Candidates, summary.Payments, summary.Messages, vesToDelete.Count, archiveFilesDeleted);

        return (TeamActionResult.Success, summary with { ArchiveFilesDeleted = archiveFilesDeleted });
    }

    /// <summary>
    /// Tracked <c>RemoveRange</c> rather than <c>ExecuteDeleteAsync</c>, throughout.
    ///
    /// <para>Three reasons, and the first is decisive: <c>ExecuteDelete</c> issues its own statement
    /// immediately, so a failure part-way through this method would leave the team half-deleted with
    /// no way back. Tracked removals all land in one <c>SaveChangesAsync</c>, which SQLite wraps in a
    /// transaction. It also keeps the Cascade relationships working (<c>ExecuteDelete</c> bypasses
    /// EF's cascade fixup), and it keeps this testable on providers that are not relational.</para>
    ///
    /// <para>The volumes make it affordable: a team's whole history is thousands of rows, not
    /// millions, and this runs once in a team's lifetime.</para>
    /// </summary>
    private async Task RemoveAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) where T : class
    {
        var rows = await query.ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            dbContext.Set<T>().RemoveRange(rows);
        }
    }

    /// <summary>
    /// The VEs this delete takes with it: those for whom the team being deleted is the only one.
    ///
    /// <para>Three exclusions, each of which would otherwise throw on save or strand something:</para>
    /// <list type="bullet">
    /// <item>a VE linked to a <b>user account</b> (<c>User.VolunteerExaminerId</c>, Restrict with a
    /// unique index) — the account survives, so its VE must too;</item>
    /// <item>a VE that another VE record was <b>merged into</b> (a Restrict self-reference) — the
    /// surviving duplicate points at it, and that pointer is cross-team by nature;</item>
    /// <item>a VE still on <b>another team's session roster</b>. Memberships and roster rows are
    /// established independently, so "only member of this team" does not imply "worked only this
    /// team's sessions" — this is the one that would be easy to miss.</item>
    /// </list>
    /// </summary>
    private async Task<List<VolunteerExaminer>> FindVesToDeleteAsync(int teamId, CancellationToken cancellationToken)
    {
        var soleMembers = await dbContext.VolunteerExaminers
            .Where(v => v.TeamMemberships.Any(m => m.TeamId == teamId)
                     && v.TeamMemberships.All(m => m.TeamId == teamId))
            .ToListAsync(cancellationToken);
        if (soleMembers.Count == 0)
        {
            return [];
        }

        var ids = soleMembers.Select(v => v.Id).ToList();
        var otherTeamSessionIds = await dbContext.Sessions.Where(s => s.TeamId != teamId).Select(s => s.Id).ToListAsync(cancellationToken);

        var linkedToAccount = await dbContext.Users
            .Where(u => u.VolunteerExaminerId != null && ids.Contains(u.VolunteerExaminerId.Value))
            .Select(u => u.VolunteerExaminerId!.Value)
            .ToListAsync(cancellationToken);
        var mergedInto = await dbContext.VolunteerExaminers
            .Where(v => v.MergedIntoVolunteerExaminerId != null && ids.Contains(v.MergedIntoVolunteerExaminerId.Value))
            .Select(v => v.MergedIntoVolunteerExaminerId!.Value)
            .ToListAsync(cancellationToken);
        var onAnotherTeamsRoster = await dbContext.SessionVolunteerExaminers
            .Where(x => ids.Contains(x.VolunteerExaminerId) && otherTeamSessionIds.Contains(x.SessionId))
            .Select(x => x.VolunteerExaminerId)
            .ToListAsync(cancellationToken);

        return [.. soleMembers.Where(v =>
            !linkedToAccount.Contains(v.Id)
            && !mergedInto.Contains(v.Id)
            && !onAnotherTeamsRoster.Contains(v.Id))];
    }

    /// <summary>
    /// Removes the ARRL archives this team holds on disk. Mike, 2026-08-21: <i>"We can't delete what
    /// ARRL has, but we can delete the files we have on the server."</i>
    ///
    /// <para>File by file from the stored relative paths, never by deleting the team's directory: the
    /// archive tree is keyed on <c>ExamToolsTeamCode</c>, which is a free-text field nothing stops two
    /// teams from sharing, and a recursive delete on a shared code would take the other team's
    /// evidence with it.</para>
    ///
    /// <para>A file that is already gone is a success — same rule as the retention purge, since row
    /// and file were never atomic.</para>
    /// </summary>
    private async Task<int> DeleteArchiveFilesAsync(int teamId, CancellationToken cancellationToken)
    {
        if (!archiveStore.IsConfigured)
        {
            return 0;
        }

        var paths = await dbContext.ArrlVecSubmissions
            .Where(a => a.TeamId == teamId)
            .Select(a => new { a.ArchiveStoredPath, a.AttachmentStoredPath })
            .ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var stored in paths.SelectMany(p => new[] { p.ArchiveStoredPath, p.AttachmentStoredPath }))
        {
            if (archiveStore.ResolveFullPath(stored) is not { } fullPath)
            {
                continue;
            }

            try
            {
                File.Delete(fullPath);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logged and carried on: a file this app cannot remove must not strand the whole
                // delete, and the row that names it is about to go either way.
                logger.LogWarning(ex, "Could not delete ARRL archive file {Path} while deleting team {TeamId}", stored, teamId);
            }
        }

        return deleted;
    }
}
