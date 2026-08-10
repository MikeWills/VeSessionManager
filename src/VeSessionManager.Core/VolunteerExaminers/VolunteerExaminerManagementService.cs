using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Every write to the VE directory (issue #142 phase 2) — contact details, tags, VEC accreditations,
/// and a membership's active state. Result-enum-returning and audit-logged, the same shape as
/// <c>CandidateActionService</c> and <c>VecManagementService</c>.
///
/// <para><b>Nothing here is ever undone by the ExamTools sync.</b> That is the contract phase 1
/// established: ExamTools owns whether a membership exists and nothing else, so an admin's edits and
/// a VE's own edits both survive the next poll. Anything added to this service must stay on the
/// app-owned side of that line.</para>
///
/// <para><b>There is no delete.</b> A person can serve several teams and their session history
/// references them by id, so removing a row would either orphan that history or rewrite who ran a
/// past session. Retiring someone is <see cref="SetMembershipActiveAsync"/>.</para>
/// </summary>
public class VolunteerExaminerManagementService(AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>
    /// Contact details live on the person and are shared by every team they serve — this deployment
    /// hosts cooperating teams, not unrelated organisations.
    ///
    /// <para><b>An admin CAN set the email here (corrected 2026-08-07).</b> It was originally locked
    /// on the grounds that it is the self-service sign-in credential — but an admin already has full
    /// write access to this person, so refusing them one field was theatre, and it left a VE with no
    /// address permanently unable to start self-service with no supported way to fix it. Discovered
    /// the moment the flow was tried for real.
    ///
    /// <para>What actually needed protecting was the VE changing it <i>unconfirmed</i>, and
    /// VeEmailChangeService handles that: their own change is confirmed from the address already on
    /// file. An admin's change is a different act by a different party, and it is audited.</para></para>
    /// </summary>
    public async Task<VeManagementResult> UpdateContactDetailsAsync(
        int volunteerExaminerId, VeContactDetails details, int userId, CancellationToken cancellationToken)
    {
        var person = await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.Id == volunteerExaminerId, cancellationToken);
        if (person is null)
        {
            return VeManagementResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Uniqueness matters as much here as in the self-service flow: sign-in resolves an address
        // to one person, so two VEs sharing one means somebody silently receives another's links.
        var email = Blank(details.Email);
        if (email is not null && await dbContext.VolunteerExaminers
                .AnyAsync(v => v.Id != person.Id && v.Email != null && v.Email.ToLower() == email.ToLower(), cancellationToken))
        {
            return VeManagementResult.EmailAlreadyInUse;
        }

        var emailChanged = !string.Equals(person.Email, email, StringComparison.OrdinalIgnoreCase);

        person.Name = details.Name.Trim();
        person.Email = email;
        person.Phone = Blank(details.Phone);
        person.AddressLine1 = Blank(details.AddressLine1);
        person.AddressLine2 = Blank(details.AddressLine2);
        person.City = Blank(details.City);
        person.State = Blank(details.State);
        person.PostalCode = Blank(details.PostalCode);
        person.DiscordUsername = Blank(details.DiscordUsername);
        person.ContactPreference = details.ContactPreference;
        person.Notes = Blank(details.Notes);
        person.UpdatedUtc = now;

        // Deliberately records that contact details changed, not what they changed to: the audit log
        // is readable by roles that are not entitled to see a VE's home address, and a diff in the
        // details column would route around the very restriction the page enforces.
        dbContext.AddAuditLog(userId, "VeContactDetailsUpdated", nameof(VolunteerExaminer), person.Id,
            $"Contact details updated for {person.CallSign ?? person.Name}." +
            // Called out specifically, unlike the other fields: this one decides who can sign in as
            // them and who receives their links, so "an admin changed it" is worth being able to find.
            (emailChanged ? " Email address was changed by an admin." : ""), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    /// <summary>
    /// The VE's own edit of their details (issue #142 phase 5). Narrower than
    /// <see cref="UpdateContactDetailsAsync"/> on purpose: <b>Notes are admin-facing and not included
    /// here</b>, so a VE can neither read nor overwrite what their team wrote about them. Email is
    /// absent too — that goes through VeEmailChangeService, confirmed from the old address.
    ///
    /// <para>Audited with a null acting user, because there is no admin involved and naming one would
    /// make the trail say something untrue.</para>
    /// </summary>
    public async Task<VeManagementResult> UpdateOwnContactDetailsAsync(
        int volunteerExaminerId, VeSelfContactDetails details, CancellationToken cancellationToken)
    {
        var person = await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.Id == volunteerExaminerId, cancellationToken);
        if (person is null)
        {
            return VeManagementResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        person.Name = string.IsNullOrWhiteSpace(details.Name) ? person.Name : details.Name.Trim();
        person.Phone = Blank(details.Phone);
        person.AddressLine1 = Blank(details.AddressLine1);
        person.AddressLine2 = Blank(details.AddressLine2);
        person.City = Blank(details.City);
        person.State = Blank(details.State);
        person.PostalCode = Blank(details.PostalCode);
        person.DiscordUsername = Blank(details.DiscordUsername);
        person.ContactPreference = details.ContactPreference;
        person.UpdatedUtc = now;

        dbContext.AddAuditLog(null, "VeContactDetailsUpdatedBySelf", nameof(VolunteerExaminer), person.Id,
            $"{person.CallSign ?? person.Name} updated their own contact details.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    /// <summary>
    /// Retire a VE from one team, or bring them back. The membership row stays either way — see the
    /// class remarks.
    /// </summary>
    public async Task<VeManagementResult> SetMembershipActiveAsync(
        int membershipId, bool isActive, int userId, CancellationToken cancellationToken)
    {
        var membership = await dbContext.VeTeamMemberships
            .Include(m => m.VolunteerExaminer)
            .Include(m => m.Team)
            .FirstOrDefaultAsync(m => m.Id == membershipId, cancellationToken);
        if (membership is null)
        {
            return VeManagementResult.NotFound;
        }

        if (membership.IsActive == isActive)
        {
            return VeManagementResult.Success;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        membership.IsActive = isActive;
        membership.InactivatedUtc = isActive ? null : now;

        dbContext.AddAuditLog(userId, isActive ? "VeMembershipReactivated" : "VeMembershipInactivated",
            nameof(VeTeamMembership), membership.Id,
            $"{membership.VolunteerExaminer.CallSign ?? membership.VolunteerExaminer.Name} " +
            $"{(isActive ? "reactivated on" : "retired from")} team {membership.Team.Name}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    /// <summary>
    /// Replaces a membership's tags wholesale — the screen posts the full set, so a diff here would
    /// only reconstruct what the caller already knows.
    /// <para>Tags are validated to belong to the membership's own team: they are a team's private
    /// vocabulary, and accepting an arbitrary id would let one team's row be labelled with another's.</para>
    /// </summary>
    public async Task<VeManagementResult> SetTagsAsync(
        int membershipId, IReadOnlyList<int> tagIds, int userId, CancellationToken cancellationToken)
    {
        var membership = await dbContext.VeTeamMemberships
            .Include(m => m.TagAssignments)
            .Include(m => m.VolunteerExaminer)
            .FirstOrDefaultAsync(m => m.Id == membershipId, cancellationToken);
        if (membership is null)
        {
            return VeManagementResult.NotFound;
        }

        var requested = tagIds.Distinct().ToList();
        var valid = await dbContext.VeTags
            .Where(t => t.TeamId == membership.TeamId && requested.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (valid.Count != requested.Count)
        {
            return VeManagementResult.TagNotOnThisTeam;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var gone in membership.TagAssignments.Where(a => !valid.Contains(a.VeTagId)).ToList())
        {
            membership.TagAssignments.Remove(gone);
            dbContext.VeTagAssignments.Remove(gone);
        }

        foreach (var added in valid.Where(id => membership.TagAssignments.All(a => a.VeTagId != id)))
        {
            membership.TagAssignments.Add(new VeTagAssignment { VeTeamMembershipId = membership.Id, VeTagId = added, CreatedUtc = now });
        }

        dbContext.AddAuditLog(userId, "VeTagsUpdated", nameof(VeTeamMembership), membership.Id,
            $"Tags updated for {membership.VolunteerExaminer.CallSign ?? membership.VolunteerExaminer.Name}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    /// <summary>
    /// Records that a VE is accredited with a VEC — presence only. Number and expiry were dropped
    /// 2026-08-09: keeping accreditation current is the VE's own job, and a date nobody refreshes
    /// would be presented as fact and start refusing people.
    /// </summary>
    public async Task<VeManagementResult> AddAccreditationAsync(
        int volunteerExaminerId, int vecId, int userId, CancellationToken cancellationToken)
    {
        var person = await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.Id == volunteerExaminerId, cancellationToken);
        if (person is null)
        {
            return VeManagementResult.NotFound;
        }

        if (!await dbContext.Vecs.AnyAsync(v => v.Id == vecId, cancellationToken))
        {
            return VeManagementResult.NotFound;
        }

        if (await dbContext.VeVecAccreditations.AnyAsync(a => a.VolunteerExaminerId == volunteerExaminerId && a.VecId == vecId, cancellationToken))
        {
            return VeManagementResult.AlreadyAccredited;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.VeVecAccreditations.Add(new VeVecAccreditation
        {
            VolunteerExaminerId = volunteerExaminerId,
            VecId = vecId,
            CreatedUtc = now
        });

        dbContext.AddAuditLog(userId, "VeAccreditationAdded", nameof(VolunteerExaminer), volunteerExaminerId,
            $"Accreditation added for {person.CallSign ?? person.Name}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    public async Task<VeManagementResult> RemoveAccreditationAsync(int accreditationId, int userId, CancellationToken cancellationToken)
    {
        var accreditation = await dbContext.VeVecAccreditations
            .Include(a => a.VolunteerExaminer)
            .FirstOrDefaultAsync(a => a.Id == accreditationId, cancellationToken);
        if (accreditation is null)
        {
            return VeManagementResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.VeVecAccreditations.Remove(accreditation);

        // Unlike a person or a membership, an accreditation row is safe to delete outright: nothing
        // references it, and a wrongly-entered one should not linger as a claim that someone is
        // accredited when they are not.
        dbContext.AddAuditLog(userId, "VeAccreditationRemoved", nameof(VolunteerExaminer), accreditation.VolunteerExaminerId,
            $"Accreditation removed for {accreditation.VolunteerExaminer.CallSign ?? accreditation.VolunteerExaminer.Name}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    // ---- Tag vocabulary (per team) ------------------------------------------------------------

    public async Task<(VeManagementResult Result, VeTag? Tag)> CreateTagAsync(int teamId, string name, int sortOrder, string? color, int userId, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return (VeManagementResult.NameRequired, null);
        }

        if (!VeTagColor.TryNormalize(color, out var normalizedColor))
        {
            return (VeManagementResult.InvalidColor, null);
        }

        if (await dbContext.VeTags.AnyAsync(t => t.TeamId == teamId && t.Name == name, cancellationToken))
        {
            return (VeManagementResult.DuplicateTagName, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var tag = new VeTag { TeamId = teamId, Name = name, SortOrder = sortOrder, Color = normalizedColor, CreatedUtc = now };
        dbContext.VeTags.Add(tag);
        await dbContext.SaveChangesAsync(cancellationToken); // assigns Id for the audit row

        dbContext.AddAuditLog(userId, "VeTagCreated", nameof(VeTag), tag.Id, $"VE tag '{name}' created.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (VeManagementResult.Success, tag);
    }

    /// <summary>
    /// Rename a tag, reorder it, or recolour it (added 2026-08-09).
    ///
    /// <para>Until this existed the only way to change a tag's order was <b>delete and re-add</b> —
    /// which cascades the assignments away, so correcting a display detail silently untagged every
    /// VE who had it. Editing in place keeps the id, so assignments are untouched by construction
    /// rather than by care.</para>
    ///
    /// <para>The duplicate-name check excludes this tag by id. Note the id is a plain <c>int</c>, not
    /// <c>int?</c>: an "exclude this row" predicate written against a nullable would match nothing at
    /// all under SQL null semantics, and EF InMemory would not reproduce it — the trap CLAUDE.md
    /// records from <c>VecManagementService.MatchCodeIsTakenAsync</c>.</para>
    /// </summary>
    public async Task<VeManagementResult> UpdateTagAsync(int tagId, string name, int sortOrder, string? color, int userId, CancellationToken cancellationToken)
    {
        var tag = await dbContext.VeTags.FirstOrDefaultAsync(t => t.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return VeManagementResult.NotFound;
        }

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return VeManagementResult.NameRequired;
        }

        if (!VeTagColor.TryNormalize(color, out var normalizedColor))
        {
            return VeManagementResult.InvalidColor;
        }

        if (await dbContext.VeTags.AnyAsync(t => t.TeamId == tag.TeamId && t.Id != tagId && t.Name == name, cancellationToken))
        {
            return VeManagementResult.DuplicateTagName;
        }

        var previousName = tag.Name;
        var changes = new List<string>();
        if (!string.Equals(previousName, name, StringComparison.Ordinal)) changes.Add($"renamed from '{previousName}'");
        if (tag.SortOrder != sortOrder) changes.Add($"order {tag.SortOrder} to {sortOrder}");
        if (!string.Equals(tag.Color, normalizedColor, StringComparison.OrdinalIgnoreCase)) changes.Add("colour changed");

        if (changes.Count == 0)
        {
            return VeManagementResult.Success;
        }

        tag.Name = name;
        tag.SortOrder = sortOrder;
        tag.Color = normalizedColor;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(userId, "VeTagUpdated", nameof(VeTag), tag.Id,
            $"VE tag '{name}' updated: {string.Join(", ", changes)}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    /// <summary>Deleting a tag removes it from everyone who had it — the assignments cascade. That is the intent: the vocabulary changed.</summary>
    public async Task<VeManagementResult> DeleteTagAsync(int tagId, int userId, CancellationToken cancellationToken)
    {
        var tag = await dbContext.VeTags.FirstOrDefaultAsync(t => t.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return VeManagementResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.VeTags.Remove(tag);
        dbContext.AddAuditLog(userId, "VeTagDeleted", nameof(VeTag), tag.Id, $"VE tag '{tag.Name}' deleted.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Contact details as one value, so the update signature doesn't grow to ten positional strings that are trivial to transpose.</summary>
public record VeContactDetails(
    string Name,
    string? Email,
    string? Phone,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? DiscordUsername,
    VeContactPreference ContactPreference,
    string? Notes);

/// <summary>What a VE may change about themselves. No Notes (admin-facing) and no Email (see VeEmailChangeService).</summary>
public record VeSelfContactDetails(
    string Name,
    string? Phone,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? DiscordUsername,
    VeContactPreference ContactPreference);

public enum VeManagementResult
{
    Success,
    NotFound,
    NameRequired,
    DuplicateTagName,
    TagNotOnThisTeam,

    /// <summary>A tag colour that isn't #RRGGBB. Rejected rather than dropped, so a bad value is never silently stored — see VeTagColor.</summary>
    InvalidColor,

    AlreadyAccredited,

    /// <summary>Another VE already uses that address — sign-in could not tell them apart.</summary>
    EmailAlreadyInUse
}
