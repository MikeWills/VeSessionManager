using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Merges two VE records that turned out to be one person.
///
/// <para>The duplicates exist by design: #142's migration merged per-team rows only when call sign
/// <i>and</i> name agreed, because a call sign reissued to a different person would otherwise fuse
/// two humans irreversibly. The #107 FRN backfill then turns that suspicion into proof — FRN is
/// unique per person, so two rows resolving to one is conclusive. This service is what a human uses
/// to act on that proof.</para>
///
/// <para><b>Everything happens in one transaction.</b> Per-row saves are right for every scan-based
/// job in this app and exactly wrong here: a half-finished merge, with some session links repointed
/// and some not, is the one outcome with no recovery.</para>
///
/// <para><b>The loser is retired, never deleted</b> — see
/// <see cref="VolunteerExaminer.MergedIntoVolunteerExaminerId"/> and the global query filter that
/// makes it vanish from every query at once.</para>
/// </summary>
public class VolunteerExaminerMergeService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<VolunteerExaminerMergeService> logger)
{
    /// <summary>
    /// What a merge would do, for the confirmation screen. Computed from the same loads the merge
    /// itself uses, so the numbers a human approves are the numbers that move.
    /// </summary>
    public async Task<(VeMergeResult Result, VeMergePreview? Preview)> PreviewAsync(
        int survivorId, int duplicateId, CancellationToken cancellationToken)
    {
        var (result, survivor, duplicate) = await LoadPairAsync(survivorId, duplicateId, cancellationToken);
        if (result != VeMergeResult.Success)
        {
            return (result, null);
        }

        var survivorSessionIds = survivor!.SessionVolunteerExaminers.Select(l => l.SessionId).ToHashSet();
        var moving = duplicate!.SessionVolunteerExaminers.Select(l => l.SessionId).Where(id => !survivorSessionIds.Contains(id)).ToList();
        var shared = duplicate.SessionVolunteerExaminers.Count - moving.Count;

        return (VeMergeResult.Success, new VeMergePreview(
            survivor.Name,
            duplicate.Name,
            moving.Count,
            shared,
            duplicate.TeamMemberships.Count,
            duplicate.VecAccreditations.Count));
    }

    public async Task<VeMergeResult> MergeAsync(int survivorId, int duplicateId, int userId, CancellationToken cancellationToken)
    {
        var (result, survivor, duplicate) = await LoadPairAsync(survivorId, duplicateId, cancellationToken);
        if (result != VeMergeResult.Success)
        {
            return result;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The invariant this whole method exists to protect, captured before anything moves.
        var distinctSessionsBefore = survivor!.SessionVolunteerExaminers.Select(l => l.SessionId)
            .Concat(duplicate!.SessionVolunteerExaminers.Select(l => l.SessionId))
            .Distinct()
            .Count();

        // Recorded because MergedIntoVolunteerExaminerId says only THAT a merge happened. Without
        // knowing which links came from which side, an un-merge could not tell whose history was
        // whose — and calling this reversible would be an overclaim.
        var movedSessionIds = duplicate.SessionVolunteerExaminers.Select(l => l.SessionId).OrderBy(id => id).ToList();

        // Captured before the transfer below clears it off the retired row.
        var duplicateFrnBefore = duplicate.Frn;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            MergeSessionLinks(survivor, duplicate);
            MergeTeamMemberships(survivor, duplicate, now);
            MergeAccreditations(survivor, duplicate);
            MergeCallSignHistory(survivor, duplicate, now);
            FillBlankContactDetails(survivor, duplicate);
            await RepointIdentityReferencesAsync(survivor, duplicate, cancellationToken);

            duplicate.MergedIntoVolunteerExaminerId = survivor.Id;
            survivor.UpdatedUtc = now;

            dbContext.AddAuditLog(userId, "VeRecordsMerged", nameof(VolunteerExaminer), survivor.Id,
                $"Merged VE {duplicate.Id} ({duplicate.CallSign ?? duplicate.Name}, FRN {duplicateFrnBefore ?? "none"}) " +
                $"into VE {survivor.Id} ({survivor.CallSign ?? survivor.Name}). Moved sessions: " +
                JsonSerializer.Serialize(movedSessionIds),
                now);

            await dbContext.SaveChangesAsync(cancellationToken);

            // Asserted against the database, not the in-memory graph — this is the promise that no
            // session history was lost, and it is worth failing a merge over. A shared session
            // legitimately collapses two links into one, which is why the check counts DISTINCT
            // sessions rather than links: the same fact recorded twice is not two facts.
            var distinctSessionsAfter = await dbContext.SessionVolunteerExaminers
                .Where(l => l.VolunteerExaminerId == survivor.Id)
                .Select(l => l.SessionId)
                .Distinct()
                .CountAsync(cancellationToken);

            if (distinctSessionsAfter != distinctSessionsBefore)
            {
                await transaction.RollbackAsync(cancellationToken);
                // The database is back where it started, but the change tracker is not: the save
                // above marked survivor, duplicate and every moved row Unchanged, so for the rest of
                // this scoped request the context would report the merge as applied while the
                // database disagrees (issue #234). Clearing is right rather than surgical here —
                // the whole unit of work is being abandoned, and there is nothing left to keep.
                dbContext.ChangeTracker.Clear();
                logger.LogError(
                    "Refusing merge of VE {DuplicateId} into VE {SurvivorId}: session history would change from {Before} to {After}",
                    duplicate.Id, survivor.Id, distinctSessionsBefore, distinctSessionsAfter);
                return VeMergeResult.SessionHistoryWouldChange;
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Merged VE {DuplicateId} into VE {SurvivorId}: {SessionCount} distinct session(s) retained",
                duplicate.Id, survivor.Id, distinctSessionsAfter);

            return VeMergeResult.Success;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Repoints the three references that identify a person to an *account* rather than to their
    /// roster history — the ones the merge used to leave pointing at the retired row (issue #250).
    ///
    /// <para><b>Why they are worse than an ordinary dangling reference.</b> The retired row is hidden
    /// by a global query filter (<c>MergedIntoVolunteerExaminerId == null</c>), so these do not
    /// merely point at stale data — the target becomes <i>invisible</i>:</para>
    ///
    /// <list type="bullet">
    ///   <item><see cref="User.VolunteerExaminerId"/> — <c>/Account/MyVeDetails</c> resolves the
    ///   signed-in user's VE through it, so the one VE who has an account would silently lose access
    ///   to their own record, permanently. Re-linking by hand is then blocked too, because
    ///   <c>IX_AspNetUsers_VolunteerExaminerId</c> is unique and filtered.</item>
    ///   <item><c>VeSelfServiceToken</c> and <c>VeEmailChangeRequest</c> — both <c>Include</c> a
    ///   <b>required</b> navigation to <see cref="VolunteerExaminer"/>, which EF renders as an INNER
    ///   JOIN. The filter applies to the joined side, so the <i>token row itself</i> disappears from
    ///   the query and an outstanding link reports "invalid or expired" rather than working.</item>
    /// </list>
    ///
    /// <para>Runs inside the merge's transaction, so it is covered by the same all-or-nothing
    /// guarantee as the rest. <c>ExecuteUpdateAsync</c> rather than load-and-assign because these are
    /// small, unconditional repoints and there is nothing to reconcile in memory — and, for the token
    /// tables, because the query filter would otherwise hide the very rows being fixed.</para>
    /// </summary>
    private async Task RepointIdentityReferencesAsync(
        VolunteerExaminer survivor, VolunteerExaminer duplicate, CancellationToken cancellationToken)
    {
        await dbContext.Users
            .Where(u => u.VolunteerExaminerId == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.VolunteerExaminerId, survivor.Id), cancellationToken);

        await dbContext.VeSelfServiceTokens
            .IgnoreQueryFilters()
            .Where(t => t.VolunteerExaminerId == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.VolunteerExaminerId, survivor.Id), cancellationToken);

        await dbContext.VeEmailChangeRequests
            .IgnoreQueryFilters()
            .Where(r => r.VolunteerExaminerId == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.VolunteerExaminerId, survivor.Id), cancellationToken);
    }

    /// <summary>
    /// Session links move wholesale, except where the survivor already has one for that session —
    /// <c>(SessionId, VolunteerExaminerId)</c> is the primary key, so those would collide. Collapsing
    /// them is not data loss: one person cannot be on a session's roster twice, and a count of 1 is
    /// the correct answer rather than a diminished one.
    /// </summary>
    private void MergeSessionLinks(VolunteerExaminer survivor, VolunteerExaminer duplicate)
    {
        var survivorSessionIds = survivor.SessionVolunteerExaminers.Select(l => l.SessionId).ToHashSet();

        foreach (var link in duplicate.SessionVolunteerExaminers.ToList())
        {
            dbContext.SessionVolunteerExaminers.Remove(link);

            if (survivorSessionIds.Add(link.SessionId))
            {
                dbContext.SessionVolunteerExaminers.Add(new SessionVolunteerExaminer
                {
                    SessionId = link.SessionId,
                    VolunteerExaminerId = survivor.Id
                });
            }
        }
    }

    /// <summary>
    /// Unique on <c>(VolunteerExaminerId, TeamId)</c>, so a team both records serve has to fold into
    /// one. Active wins over retired — being on one record's active roster means they serve that
    /// team — and the tags union, since each side's tags are things a human deliberately applied.
    /// </summary>
    private void MergeTeamMemberships(VolunteerExaminer survivor, VolunteerExaminer duplicate, DateTime now)
    {
        foreach (var membership in duplicate.TeamMemberships.ToList())
        {
            var existing = survivor.TeamMemberships.FirstOrDefault(m => m.TeamId == membership.TeamId);
            if (existing is null)
            {
                membership.VolunteerExaminerId = survivor.Id;
                survivor.TeamMemberships.Add(membership);
                continue;
            }

            if (membership.IsActive && !existing.IsActive)
            {
                existing.IsActive = true;
                existing.InactivatedUtc = null;
            }

            foreach (var assignment in membership.TagAssignments.ToList())
            {
                if (existing.TagAssignments.All(a => a.VeTagId != assignment.VeTagId))
                {
                    existing.TagAssignments.Add(new VeTagAssignment
                    {
                        VeTeamMembershipId = existing.Id,
                        VeTagId = assignment.VeTagId,
                        CreatedUtc = now
                    });
                }

                dbContext.VeTagAssignments.Remove(assignment);
            }

            dbContext.VeTeamMemberships.Remove(membership);
        }
    }

    /// <summary>
    /// Unique on <c>(VolunteerExaminerId, VecId)</c>. Since an accreditation is now presence-only,
    /// two rows for the same VEC carry identical information and the duplicate is simply dropped —
    /// there is no longer a richer one to prefer.
    /// </summary>
    private void MergeAccreditations(VolunteerExaminer survivor, VolunteerExaminer duplicate)
    {
        foreach (var accreditation in duplicate.VecAccreditations.ToList())
        {
            var existing = survivor.VecAccreditations.FirstOrDefault(a => a.VecId == accreditation.VecId);
            if (existing is null)
            {
                accreditation.VolunteerExaminerId = survivor.Id;
                survivor.VecAccreditations.Add(accreditation);
                continue;
            }

            dbContext.VeVecAccreditations.Remove(accreditation);
        }
    }

    /// <summary>History moves across, and the duplicate's own call sign becomes history if it differs — it is a call sign this person demonstrably used.</summary>
    private static void MergeCallSignHistory(VolunteerExaminer survivor, VolunteerExaminer duplicate, DateTime now)
    {
        foreach (var history in duplicate.CallSignHistory.ToList())
        {
            history.VolunteerExaminerId = survivor.Id;
            survivor.CallSignHistory.Add(history);
        }

        if (!string.IsNullOrWhiteSpace(duplicate.CallSign)
            && !string.Equals(duplicate.CallSign, survivor.CallSign, StringComparison.OrdinalIgnoreCase)
            && survivor.CallSignHistory.All(h => !string.Equals(h.CallSign, duplicate.CallSign, StringComparison.OrdinalIgnoreCase)))
        {
            survivor.CallSignHistory.Add(new VeCallSignHistory
            {
                VolunteerExaminerId = survivor.Id,
                CallSign = duplicate.CallSign!,
                FirstSeenUtc = duplicate.CreatedUtc,
                ReplacedUtc = now
            });
        }
    }

    /// <summary>
    /// <b>Fill blanks, never overwrite.</b> The survivor keeps everything a human typed on it; the
    /// duplicate's values only fill fields the survivor left empty. Anything genuinely conflicting
    /// stays visible in the audit entry rather than being silently resolved by whichever record
    /// happened to be picked as the survivor.
    /// </summary>
    private static void FillBlankContactDetails(VolunteerExaminer survivor, VolunteerExaminer duplicate)
    {
        survivor.Email ??= duplicate.Email;
        survivor.Phone ??= duplicate.Phone;
        survivor.AddressLine1 ??= duplicate.AddressLine1;
        survivor.AddressLine2 ??= duplicate.AddressLine2;
        survivor.City ??= duplicate.City;
        survivor.State ??= duplicate.State;
        survivor.PostalCode ??= duplicate.PostalCode;
        survivor.DiscordUsername ??= duplicate.DiscordUsername;

        // FRN is transferred, not copied. It is unique across VolunteerExaminers, so leaving it on
        // the retired row would violate that index the moment both hold it — and an FRN identifies
        // the person, who is now the survivor. The original value stays in the audit entry.
        if (survivor.Frn is null && duplicate.Frn is not null)
        {
            survivor.Frn = duplicate.Frn;
        }

        duplicate.Frn = null;

        // The conflict is resolved by the merge itself: the FRN both records claimed now sits on one
        // person, so the note recording that they collided has nothing left to say.
        survivor.ConflictingFrn = null;
        duplicate.ConflictingFrn = null;
        survivor.Notes = string.IsNullOrWhiteSpace(duplicate.Notes)
            ? survivor.Notes
            : string.IsNullOrWhiteSpace(survivor.Notes) ? duplicate.Notes : survivor.Notes + "\n\n" + duplicate.Notes;
    }

    private async Task<(VeMergeResult Result, VolunteerExaminer? Survivor, VolunteerExaminer? Duplicate)> LoadPairAsync(
        int survivorId, int duplicateId, CancellationToken cancellationToken)
    {
        if (survivorId == duplicateId)
        {
            return (VeMergeResult.SameRecord, null, null);
        }

        var people = await dbContext.VolunteerExaminers
            .Include(v => v.SessionVolunteerExaminers)
            .Include(v => v.TeamMemberships).ThenInclude(m => m.TagAssignments)
            .Include(v => v.VecAccreditations)
            .Include(v => v.CallSignHistory)
            .Where(v => v.Id == survivorId || v.Id == duplicateId)
            .ToListAsync(cancellationToken);

        var survivor = people.FirstOrDefault(v => v.Id == survivorId);
        var duplicate = people.FirstOrDefault(v => v.Id == duplicateId);

        if (survivor is null || duplicate is null)
        {
            return (VeMergeResult.NotFound, null, null);
        }

        // A hard block, not a warning: two different FRNs is FCC saying these are two people, which
        // is stronger evidence against the merge than a matching name is for it.
        if (survivor.Frn is { } survivorFrn && duplicate.Frn is { } duplicateFrn
            && !string.Equals(survivorFrn, duplicateFrn, StringComparison.OrdinalIgnoreCase))
        {
            return (VeMergeResult.DifferentFrns, null, null);
        }

        return (VeMergeResult.Success, survivor, duplicate);
    }
}

/// <summary>The numbers a human approves before an irreversible action — real counts, not a generic "are you sure?".</summary>
public record VeMergePreview(
    string SurvivorName,
    string DuplicateName,
    int SessionsMoving,
    int SessionsAlreadyShared,
    int TeamMembershipsMoving,
    int AccreditationsMoving);

public enum VeMergeResult
{
    Success,
    NotFound,
    SameRecord,

    /// <summary>Both hold an FRN, and they differ — FCC says these are two people.</summary>
    DifferentFrns,

    /// <summary>The conservation check failed, so the merge was rolled back rather than committed.</summary>
    SessionHistoryWouldChange
}
