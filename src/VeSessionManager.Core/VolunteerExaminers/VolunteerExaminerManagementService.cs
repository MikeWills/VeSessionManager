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
    /// hosts cooperating teams, not unrelated organizations.
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
    /// <param name="actingUserId">
    /// The signed-in user making the change, when there is one — the in-app "My VE details" page
    /// (#226). Null for the self-service flow reached by an emailed link, where no admin account is
    /// involved and naming one would make the trail say something untrue.
    /// </param>
    public async Task<VeManagementResult> UpdateOwnContactDetailsAsync(
        int volunteerExaminerId, VeSelfContactDetails details, CancellationToken cancellationToken, int? actingUserId = null)
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

        // A returning VE stops looking purged (#313). PiiPurgedUtc is the purge pass's idempotency
        // guard, so leaving it set would mean these freshly re-entered details are never eligible
        // again — the record would carry a stamp saying "cleared" while holding a home address.
        person.PiiPurgedUtc = null;

        dbContext.AddAuditLog(actingUserId, "VeContactDetailsUpdatedBySelf", nameof(VolunteerExaminer), person.Id,
            $"{person.CallSign ?? person.Name} updated their own contact details"
            + (actingUserId is null ? " (self-service link)." : " while signed in."), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    /// <summary>
    /// Sets an email address on a VE record that has none, for a signed-in user editing their own
    /// linked record (#226).
    ///
    /// <para><b>Why this is not VeEmailChangeService.</b> That flow mails a confirmation to the
    /// <i>existing</i> address, so approval comes from the mailbox already on file — exactly right for
    /// a change, and structurally impossible for a first address. It returns <c>NoCurrentEmail</c> and
    /// stops. Without a path like this one a VE with no email can never acquire one by their own hand,
    /// because the self-service link that would let them is itself sent by email. One VE of 176 has an
    /// address on file, so that is not a corner case, it is the normal state.</para>
    ///
    /// <para><b>Why setting it directly is acceptable here, and only here.</b> The caller is
    /// authenticated in the admin app and was linked to this VE record by an administrator — a
    /// stronger claim than possession of an emailed link. And there is no address to divert: the risk
    /// the confirmation dance defends against is redirecting someone's existing mail, which cannot
    /// apply when the field is empty.</para>
    ///
    /// <para><b>Refuses when an address already exists</b>, so there is exactly one way to change a
    /// known-good email and it is the confirmed one. Two paths to the same field with different
    /// safety, the weaker one reached by whoever is already signed in, is how the careful path stops
    /// being the one that gets used.</para>
    /// </summary>
    public async Task<VeManagementResult> SetOwnEmailWhenUnsetAsync(
        int volunteerExaminerId, string email, int actingUserId, CancellationToken cancellationToken)
    {
        var person = await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.Id == volunteerExaminerId, cancellationToken);
        if (person is null)
        {
            return VeManagementResult.NotFound;
        }

        if (!string.IsNullOrWhiteSpace(person.Email))
        {
            return VeManagementResult.EmailAlreadySet;
        }

        email = (email ?? "").Trim();
        if (email.Length == 0 || !email.Contains('@'))
        {
            return VeManagementResult.InvalidEmail;
        }

        // Same uniqueness rule as every other write to this field: sign-in resolves an address to one
        // person, so two VEs sharing one means somebody silently receives another's links.
        var lowered = email.ToLowerInvariant();
        if (await dbContext.VolunteerExaminers
                .AnyAsync(v => v.Id != person.Id && v.Email != null && v.Email.ToLower() == lowered, cancellationToken))
        {
            return VeManagementResult.EmailAlreadyInUse;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        person.Email = email;
        person.UpdatedUtc = now;

        // The address itself is recorded. It is the first one on file, and what it was set to is the
        // whole audit value if links later turn out to be going somewhere unexpected.
        dbContext.AddAuditLog(actingUserId, "VeEmailSetBySelf", nameof(VolunteerExaminer), person.Id,
            $"{person.CallSign ?? person.Name} set their own email to {email} (no previous address on file).", now);
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
    /// <param name="userId">The admin who did it, or <b>null when the VE did it themselves</b> from
    /// self-service — the same convention <c>VeEmailChangeService</c> uses, and what decides whether
    /// the audit entry reads "…BySelf".</param>
    public async Task<VeManagementResult> AddAccreditationAsync(
        int volunteerExaminerId, int vecId, int? userId, CancellationToken cancellationToken)
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

        dbContext.AddAuditLog(userId, userId is null ? "VeAccreditationAddedBySelf" : "VeAccreditationAdded",
            nameof(VolunteerExaminer), volunteerExaminerId,
            $"Accreditation added for {person.CallSign ?? person.Name}{(userId is null ? " by the VE themselves" : "")}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    /// <param name="userId">Null when the VE removed it themselves — see AddAccreditationAsync.</param>
    /// <param name="mustBelongToVolunteerExaminerId">Set by the self-service caller. An accreditation
    /// id is just a number in a form, and without this a signed-in VE could delete someone else's
    /// simply by changing it. The admin path passes null because it is already authorised for every
    /// VE it can see.</param>
    public async Task<VeManagementResult> RemoveAccreditationAsync(
        int accreditationId, int? userId, CancellationToken cancellationToken, int? mustBelongToVolunteerExaminerId = null)
    {
        var accreditation = await dbContext.VeVecAccreditations
            .Include(a => a.VolunteerExaminer)
            .FirstOrDefaultAsync(a => a.Id == accreditationId, cancellationToken);
        if (accreditation is null)
        {
            return VeManagementResult.NotFound;
        }

        if (mustBelongToVolunteerExaminerId is { } ownerId && accreditation.VolunteerExaminerId != ownerId)
        {
            // Deliberately NotFound rather than a distinct "not yours": a VE probing ids learns
            // nothing about whether one exists.
            return VeManagementResult.NotFound;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.VeVecAccreditations.Remove(accreditation);

        // Unlike a person or a membership, an accreditation row is safe to delete outright: nothing
        // references it, and a wrongly-entered one should not linger as a claim that someone is
        // accredited when they are not.
        dbContext.AddAuditLog(userId, userId is null ? "VeAccreditationRemovedBySelf" : "VeAccreditationRemoved",
            nameof(VolunteerExaminer), accreditation.VolunteerExaminerId,
            $"Accreditation removed for {accreditation.VolunteerExaminer.CallSign ?? accreditation.VolunteerExaminer.Name}{(userId is null ? " by the VE themselves" : "")}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    // ---- Tag vocabulary (per team) ------------------------------------------------------------

    /// <param name="discordRoleId">The Discord role that means this tag, or null for a hand-managed tag Discord never touches (#519).</param>
    /// <param name="discordRoleName">That role's current name, stored as a display snapshot. Ignored when <paramref name="discordRoleId"/> is null.</param>
    public async Task<(VeManagementResult Result, VeTag? Tag)> CreateTagAsync(int teamId, string name, int sortOrder, string? color, ulong? discordRoleId, string? discordRoleName, int userId, CancellationToken cancellationToken)
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

        if (await DiscordRoleIsTakenAsync(teamId, discordRoleId, 0, cancellationToken))
        {
            return (VeManagementResult.DuplicateDiscordRole, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var tag = new VeTag
        {
            TeamId = teamId,
            Name = name,
            SortOrder = sortOrder,
            Color = normalizedColor,
            DiscordRoleId = discordRoleId,
            DiscordRoleName = discordRoleId is null ? null : discordRoleName,
            CreatedUtc = now,
        };
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
    /// <param name="discordRoleId">The Discord role that means this tag, or null to unmap it — which is how a tag is taken back off the sync entirely (#519).</param>
    /// <param name="discordRoleName">That role's current name, stored as a display snapshot. Ignored when <paramref name="discordRoleId"/> is null.</param>
    public async Task<VeManagementResult> UpdateTagAsync(int tagId, string name, int sortOrder, string? color, ulong? discordRoleId, string? discordRoleName, int userId, CancellationToken cancellationToken)
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

        if (await DiscordRoleIsTakenAsync(tag.TeamId, discordRoleId, tagId, cancellationToken))
        {
            return VeManagementResult.DuplicateDiscordRole;
        }

        var previousName = tag.Name;
        var changes = new List<string>();
        if (!string.Equals(previousName, name, StringComparison.Ordinal)) changes.Add($"renamed from '{previousName}'");
        if (tag.SortOrder != sortOrder) changes.Add($"order {tag.SortOrder} to {sortOrder}");
        if (!string.Equals(tag.Color, normalizedColor, StringComparison.OrdinalIgnoreCase)) changes.Add("colour changed");

        // The Discord mapping has to be in this list, not just assigned below: the early return makes
        // "nothing changed" mean "save nothing", so a role-only edit would report Success and leave
        // the tag unmapped. Pinned by MappingAnExistingTagIsSaved_EvenThoughNothingElseChanged.
        if (tag.DiscordRoleId != discordRoleId)
        {
            changes.Add(discordRoleId is null
                ? "Discord role unmapped"
                : $"mapped to Discord role {discordRoleName ?? discordRoleId.Value.ToString()}");
        }
        else if (discordRoleId is not null && !string.Equals(tag.DiscordRoleName, discordRoleName, StringComparison.Ordinal))
        {
            // Same role, renamed in Discord. Worth saving (the screen reads this snapshot) and worth
            // an audit line, since the mapping itself did not move.
            changes.Add($"Discord role now called '{discordRoleName}'");
        }

        if (changes.Count == 0)
        {
            return VeManagementResult.Success;
        }

        tag.Name = name;
        tag.SortOrder = sortOrder;
        tag.Color = normalizedColor;
        tag.DiscordRoleId = discordRoleId;
        tag.DiscordRoleName = discordRoleId is null ? null : discordRoleName;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        dbContext.AddAuditLog(userId, "VeTagUpdated", nameof(VeTag), tag.Id,
            $"VE tag '{name}' updated: {string.Join(", ", changes)}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return VeManagementResult.Success;
    }

    /// <summary>
    /// Is another tag on this team already mapped to <paramref name="discordRoleId"/>? (#519)
    ///
    /// <para><b>Null is answered before the query, not by it.</b> An unmapped tag is always allowed —
    /// most tags are — but "is any other tag also NULL" is not the question, and asking it in SQL
    /// would return false anyway: <c>DiscordRoleId = NULL</c> is NULL, never true. The early return is
    /// the behaviour, not an optimisation.</para>
    ///
    /// <para><paramref name="excludingTagId"/> is a plain <c>int</c> taking <c>0</c> on the create
    /// path, never <c>int?</c>. The same SQL null semantics turn <c>t.Id != null</c> into a predicate
    /// that matches nothing, waving every duplicate through — and EF InMemory evaluates it as plain
    /// LINQ and passes, so the tests would agree with the bug. See CLAUDE.md's note on
    /// <c>VecManagementService.MatchCodeIsTakenAsync</c>, where exactly this shipped.</para>
    /// </summary>
    private async Task<bool> DiscordRoleIsTakenAsync(int teamId, ulong? discordRoleId, int excludingTagId, CancellationToken cancellationToken)
    {
        if (discordRoleId is null)
        {
            return false;
        }

        return await dbContext.VeTags.AnyAsync(
            t => t.TeamId == teamId && t.Id != excludingTagId && t.DiscordRoleId == discordRoleId,
            cancellationToken);
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

    /// <summary>Another tag on this team already claims that Discord role (#519). One role means one tag — see VeTag.DiscordRoleId.</summary>
    DuplicateDiscordRole,

    /// <summary>A tag colour that isn't #RRGGBB. Rejected rather than dropped, so a bad value is never silently stored — see VeTagColor.</summary>
    InvalidColor,

    AlreadyAccredited,

    /// <summary>Another VE already uses that address — sign-in could not tell them apart.</summary>
    EmailAlreadyInUse,

    /// <summary>
    /// The record already has an address, so the change must go through VeEmailChangeService's
    /// confirmation rather than being written directly. See SetOwnEmailWhenUnsetAsync.
    /// </summary>
    EmailAlreadySet,

    /// <summary>Not an address at all. Rejected rather than stored, since nothing downstream would report it.</summary>
    InvalidEmail
}
