using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: user create/role/manager/deactivate management. Deactivation reuses ASP.NET Core
/// Identity's own LockoutEnd/LockoutEnabled columns (no new schema) — SignInManager already
/// enforces lockout on both the password and external-login sign-in paths (PreSignInCheck calls
/// IsLockedOutAsync before validating credentials), so setting LockoutEnd = MaxValue is sufficient
/// on its own. No invite/reset-email flow exists in this app — an admin creating a user sets and
/// communicates the initial password out-of-band, same spirit as DevAuthSeeder's shared dev
/// password; that's an explicit scope cut for this phase, not an oversight.
/// </summary>
public class UserManagementService(UserManager<User> userManager, AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>A brand-new user starts with zero team memberships — team assignment is now a
    /// separate action (SetTeamsAsync), same as role assignment already was, since a user can belong
    /// to more than one team (issue #19).</summary>
    public async Task<(UserActionResult Result, User? User)> CreateAsync(string email, string name, UserRole role, string initialPassword, int actingUserId, CancellationToken cancellationToken, string? callSign = null)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return (UserActionResult.DuplicateEmail, null);
        }

        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Name = name,
            CallSign = CallSign.NormalizeFormat(callSign),
            Role = role,
            // The admin picked this password, so the owner must replace it before doing anything
            // else. Cleared by ChangePasswordAsync below / the self-service page.
            MustChangePassword = true
        };

        var result = await userManager.CreateAsync(user, initialPassword);
        if (!result.Succeeded)
        {
            return (UserActionResult.InvalidPassword, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserCreated", user.Id, $"User '{email}' created with role {role}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (UserActionResult.Success, user);
    }

    /// <summary>
    /// A signed-in user changing their own password. Verifies the current one (UserManager does it),
    /// applies the same password policy as account creation, and clears
    /// <see cref="User.MustChangePassword"/>.
    ///
    /// <para>Deliberately separate from PasswordResetService: that flow proves identity by emailing a
    /// token, and is for someone who <i>cannot</i> sign in. This one is for someone already signed in,
    /// where the current password is the proof — which matters on a deployment with no system SMTP,
    /// where the emailed route does not work at all.</para>
    /// </summary>
    public async Task<UserActionResult> ChangeOwnPasswordAsync(
        int userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        // An OAuth account has no local password to change; their provider owns the credential.
        if (!await userManager.HasPasswordAsync(user))
        {
            return UserActionResult.NoLocalPassword;
        }

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            // Wrong current password and a policy violation are both surfaced as one failure to the
            // caller, which decides the wording. Nothing here distinguishes them for the user.
            return UserActionResult.InvalidPassword;
        }

        user.MustChangePassword = false;
        await userManager.UpdateAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Actor and subject are the same person, which is the point: this is the one password change
        // nobody else performed.
        AddAudit(user.Id, "UserPasswordChanged", user.Id, $"User {user.Id} changed their own password.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    /// <summary>
    /// Revokes every other signed-in session for this user by rotating their security stamp.
    ///
    /// <para><b>Why this exists alongside "Remember me" rather than after it (#340).</b> A remembered
    /// session lasts 30 days, so a lost or stolen phone stays signed in for a month unless there is
    /// a way to end it. Before this, the only tool was deactivating the whole account — which also
    /// locks the owner out, and needs an administrator.</para>
    ///
    /// <para><b>How the revocation actually lands.</b> The cookie is self-contained; nothing on the
    /// server tracks it. What ends it is <c>SecurityStampValidator</c>, which re-checks the stamp in
    /// the cookie against the database on a rolling interval (30 minutes by default) and rejects the
    /// principal when they differ. So this is not instant — worth saying plainly, because "signed out
    /// everywhere" implies it is. The same mechanism already backs account deactivation.</para>
    ///
    /// <para>The caller is responsible for re-signing the current browser in
    /// (<c>SignInManager.RefreshSignInAsync</c>); otherwise the person who just clicked the button
    /// is logged out too, which reads as the action having failed.</para>
    /// </summary>
    public async Task<UserActionResult> SignOutOtherSessionsAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        await userManager.UpdateSecurityStampAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        // Actor and subject are the same person by construction: this action only ever acts on the
        // caller's own account, and there is no admin-facing variant.
        AddAudit(user.Id, "UserSignedOutOtherSessions", user.Id,
            $"User {user.Id} revoked their other signed-in sessions.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    public async Task<UserActionResult> SetRoleAsync(int targetUserId, UserRole newRole, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        user.Role = newRole;
        await userManager.UpdateAsync(user);

        // The role is baked into the auth cookie at sign-in (AppClaimsPrincipalFactory), so changing
        // the column alone leaves a demoted admin holding a claim that still says SystemAdmin (#257).
        // Rotating the stamp invalidates their live sessions, which is what DeactivateAsync already
        // does and what "changed their role" is understood to mean.
        //
        // Not instant: SecurityStampValidatorOptions.ValidationInterval is the framework default of
        // 30 minutes, so that is the window, not the 8-hour cookie lifetime. Most admin pages reload
        // the User and re-check user.Role, so they fail closed immediately regardless — SystemSettings
        // and Vecs did not, and now do.
        await userManager.UpdateSecurityStampAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserRoleChanged", user.Id, $"User {user.Id} role set to {newRole}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    /// <summary>
    /// Sets (or clears) the account holder's call sign. Blank input clears rather than storing an
    /// empty string, so "no call sign" has one representation. Stored upper-invariant to match
    /// VolunteerExaminer.CallSign's existing convention.
    /// </summary>
    public async Task<UserActionResult> SetCallSignAsync(int targetUserId, string? callSign, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        user.CallSign = CallSign.NormalizeFormat(callSign);
        await userManager.UpdateAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserCallSignChanged", user.Id,
            $"User {user.Id} call sign set to {user.CallSign ?? "(none)"}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    /// <summary>
    /// The VolunteerExaminer rows an acting admin may name, scoped through team membership (#239).
    ///
    /// <para><b>Null means every team, not no teams.</b> That is
    /// <c>AdminAccessScope.GetEffectiveTeamIds</c>'s contract for a SystemAdmin, and writing this
    /// guard as <c>allowedTeamIds?.Contains(...) ?? false</c> is the documented way to accidentally
    /// lock SystemAdmins out of everything (see CLAUDE.md).</para>
    ///
    /// <para>Membership is not filtered on <c>IsActive</c>: a retired member of your own team is
    /// still your team's record to link. The question here is whose record it is, not whether they
    /// are currently serving.</para>
    /// </summary>
    private IQueryable<VolunteerExaminer> ScopedVolunteerExaminers(IReadOnlyList<int>? allowedTeamIds) =>
        allowedTeamIds is null
            ? dbContext.VolunteerExaminers
            : dbContext.VolunteerExaminers.Where(v => v.TeamMemberships.Any(m => allowedTeamIds.Contains(m.TeamId)));

    /// <summary>
    /// The VE record this login most likely belongs to, or null when there is no unambiguous answer.
    ///
    /// <para>A <b>suggestion</b>, never applied on its own — see SetVolunteerExaminerAsync. Returns
    /// null in every case where a human should look:</para>
    /// <list type="bullet">
    ///   <item>the user has no call sign recorded, so there is nothing to match on;</item>
    ///   <item>the call sign is not call-sign-shaped — ExamTools' literal "&lt;UNKNOWN&gt;" is shared
    ///         by every VE it cannot identify, and treating it as an identity once fused two
    ///         different people;</item>
    ///   <item><b>more than one VE record holds that call sign.</b> The directory already surfaces
    ///         those as possible duplicates precisely because they may be one person or two, and
    ///         guessing here would pick one at random;</item>
    ///   <item>the matched record is already linked to somebody else.</item>
    /// </list>
    /// </summary>
    public async Task<VolunteerExaminer?> SuggestVolunteerExaminerAsync(
        int targetUserId, IReadOnlyList<int>? allowedTeamIds, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);

        // Normalize, not NormalizeFormat: this value is about to identify a person, which is exactly
        // the distinction those two helpers exist to draw. User.CallSign is stored format-only
        // because it was display data (#166) — reading it as identity is a stricter question and
        // needs the stricter check.
        if (CallSign.Normalize(user?.CallSign) is not { } callSign)
        {
            return null;
        }

        // Scoped to the acting admin's own teams, for the same reason SetVolunteerExaminerAsync is
        // (#239) — and it has to be the same scope, or the page offers a "link this VE" button that
        // the setter then refuses. Note the count check below is deliberately applied AFTER scoping:
        // two matches inside one team is still ambiguous and still returns nothing.
        var matches = await ScopedVolunteerExaminers(allowedTeamIds)
            .Where(v => v.CallSign != null && v.CallSign.ToLower() == callSign.ToLower() && v.MergedIntoVolunteerExaminerId == null)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (matches.Count != 1)
        {
            return null;
        }

        var alreadyLinked = await dbContext.Users
            .AnyAsync(u => u.VolunteerExaminerId == matches[0].Id && u.Id != targetUserId, cancellationToken);

        return alreadyLinked ? null : matches[0];
    }

    /// <summary>
    /// Links this login to the VolunteerExaminer record describing the same person, or clears the
    /// link when <paramref name="volunteerExaminerId"/> is null (#224).
    ///
    /// <para><b>Deliberately not automatic.</b> A call-sign match is a strong suggestion and a poor
    /// decision: the FCC reissues call signs, so a match can be a different person who now holds the
    /// same one. The UI offers the match; a human confirms it. Binding the wrong person here would
    /// be quiet and would misdirect any future notification.</para>
    ///
    /// <para>Grants no access. See <see cref="User.VolunteerExaminerId"/>.</para>
    /// </summary>
    public async Task<UserActionResult> SetVolunteerExaminerAsync(
        int targetUserId, int? volunteerExaminerId, int actingUserId,
        IReadOnlyList<int>? allowedTeamIds, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        var description = "(none)";
        if (volunteerExaminerId is { } veId)
        {
            // Scoped to the acting admin's reachable VEs (#239). The caller's AuthorizeManageAsync
            // authorizes the *target user*; this id is a second, independent object arriving from the
            // same form, and nothing checked it. Existence plus "not already claimed" is not
            // authorization — every VE row on the deployment satisfies both.
            //
            // The link grants no access, so this is not a privilege-escalation path. What it does is
            // permanently claim another team's record: the rightful team then gets
            // VolunteerExaminerAlreadyLinked and cannot link their own person without an admin
            // unpicking it.
            //
            // NotFound rather than a distinct "not yours" result, deliberately — the two are
            // indistinguishable to the caller by design, so the response cannot be used to test
            // whether a given id exists on some other team.
            var person = await ScopedVolunteerExaminers(allowedTeamIds)
                .FirstOrDefaultAsync(v => v.Id == veId, cancellationToken);
            if (person is null)
            {
                return UserActionResult.NotFound;
            }

            // Checked here as well as by the unique index, so the caller gets a usable answer rather
            // than a DbUpdateException — and so the message can name who already holds it.
            var takenBy = await dbContext.Users
                .FirstOrDefaultAsync(u => u.VolunteerExaminerId == veId && u.Id != targetUserId, cancellationToken);
            if (takenBy is not null)
            {
                return UserActionResult.VolunteerExaminerAlreadyLinked;
            }

            description = $"{person.CallSign ?? "(no call sign)"} — {person.Name}";
        }

        user.VolunteerExaminerId = volunteerExaminerId;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserVolunteerExaminerLinked", user.Id,
            $"User {user.Id} linked to VE record {description}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    /// <summary>Replaces a TeamAdmin/SessionManager's team memberships wholesale — the actual
    /// mechanism behind issue #19 (a Session Manager can belong to multiple teams). Diffs the
    /// requested set against what's currently stored so unaffected rows are left untouched.</summary>
    public async Task<UserActionResult> SetTeamsAsync(int targetUserId, IReadOnlyList<int> teamIds, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.Include(u => u.UserTeams).FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken);
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        var requestedIds = teamIds.Distinct().ToHashSet();
        var currentIds = user.UserTeams.Select(ut => ut.TeamId).ToHashSet();

        foreach (var toRemove in user.UserTeams.Where(ut => !requestedIds.Contains(ut.TeamId)).ToList())
        {
            user.UserTeams.Remove(toRemove);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var toAdd in requestedIds.Where(id => !currentIds.Contains(id)))
        {
            user.UserTeams.Add(new UserTeam { UserId = user.Id, TeamId = toAdd, CreatedUtc = now });
        }

        AddAudit(actingUserId, "UserTeamsChanged", user.Id, $"User {user.Id}'s teams set to [{string.Join(", ", requestedIds.OrderBy(id => id))}].", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    /// <summary>
    /// Turns another user's two-factor authentication off — the lost-phone escape hatch (#356).
    ///
    /// <para><b>This is the whole reason enrolment could stay optional and still be safe.</b> Without
    /// it, someone who loses their authenticator and has used up their recovery codes is locked out
    /// permanently, and on a deployment where system SMTP has historically not been configured there
    /// is no emailed route back either.</para>
    ///
    /// <para><b>It is also, unavoidably, a way to remove someone else's second factor.</b> That is
    /// why it is restricted to callers who could already reset the account's password — an admin who
    /// can do that can take the account regardless, so this grants no new reach. Callers enforce
    /// that; this method assumes it, exactly as SetRoleAsync does.</para>
    ///
    /// <para>Resets the authenticator key rather than only clearing the flag, so an app still
    /// holding the old secret cannot be used to re-enable silently; rotates the security stamp, which
    /// ends live sessions and invalidates any trusted-device cookies; and audits loudly, because
    /// "an admin removed a second factor from an account" is exactly the kind of event the log
    /// exists for.</para>
    /// </summary>
    public async Task<UserActionResult> ClearTwoFactorAsync(int targetUserId, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        if (!user.TwoFactorEnabled)
        {
            // Idempotent rather than an error: two admins clicking the same button during an
            // incident should not produce a failure message that reads like something went wrong.
            return UserActionResult.Success;
        }

        await userManager.SetTwoFactorEnabledAsync(user, false);
        await userManager.ResetAuthenticatorKeyAsync(user);
        await userManager.UpdateSecurityStampAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "TwoFactorClearedByAdmin", user.Id,
            $"Two-factor authentication cleared for user {user.Id} by an administrator.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    public async Task<UserActionResult> SetManagerAsync(int teamLeadUserId, int? managerUserId, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.Include(u => u.UserTeams).FirstOrDefaultAsync(u => u.Id == teamLeadUserId, cancellationToken);
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        // **The manager link is a record of who a lead reports to. It grants no access.**
        //
        // It used to decide a TeamLead's team scope too, and the validation here defended that: the
        // manager had to share a team with the lead, so a TeamAdmin could not widen a lead's
        // visibility by picking an outsider. Scope now comes from the lead's own team assignment
        // (see SessionAccessScope), because a manager can belong to several teams while a lead
        // belongs to one - inheriting the manager's set handed the lead every other team that
        // manager worked on.
        //
        // With no access riding on it, the only thing left worth checking is that the person named
        // can actually manage someone. A team-overlap rule here would now block a legitimate
        // reporting line for no security benefit - which is exactly what it did (2026-08-07).
        if (managerUserId is not null)
        {
            var manager = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == managerUserId.Value, cancellationToken);
            if (manager is null || manager.Role is not (UserRole.SessionManager or UserRole.TeamAdmin))
            {
                return UserActionResult.InvalidManager;
            }
        }

        user.ManagedByUserId = managerUserId;
        await userManager.UpdateAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserManagerAssigned", user.Id, $"User {user.Id}'s manager set to {(managerUserId?.ToString() ?? "none")}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    public async Task<UserActionResult> DeactivateAsync(int targetUserId, int actingUserId, CancellationToken cancellationToken)
    {
        if (targetUserId == actingUserId)
        {
            return UserActionResult.CannotDeactivateSelf;
        }

        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);

        // Lockout alone only blocks *future* sign-ins (SignInManager's PreSignInCheck) — it does
        // nothing about a cookie this user already holds. Bumping the security stamp invalidates
        // that existing cookie on its next SecurityStampValidator check (default ~30 min), so
        // deactivation actually revokes access instead of just blocking the next login attempt.
        await userManager.UpdateSecurityStampAsync(user);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserDeactivated", user.Id, $"User {user.Id} deactivated.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    /// <summary>
    /// Permanently removes an account that has no history (#188). Deactivation remains the normal
    /// path; this exists for the mistyped or throwaway account that would otherwise be a dead row
    /// forever.
    ///
    /// <para><b>"No history" needed defining before this could be built, and the obvious reading does
    /// not work.</b> Fourteen foreign keys reference User, every one <c>Restrict</c> — and one of
    /// them is <c>AuditLog.UserId</c>, which every account carries from creation onwards
    /// (<c>BootstrapAdminCommand</c> self-attributes its own <c>UserCreated</c> entry). A literal
    /// "nothing references this user" test is never true, so the button would have silently never
    /// fired.</para>
    ///
    /// <para><b>The decision (option 1 of the three in #188, 2026-08-15):</b> the account's own
    /// lifecycle rows — the audit entries <i>about</i> this user — are deleted with it, and a fresh
    /// entry naming the removed email records that it happened. Its existence and its removal stay on
    /// the record; the dead row goes. Audit rows where this user <i>acted on something else</i> are
    /// history and block the delete, which is what stops this becoming a way to erase what somebody
    /// did.</para>
    ///
    /// <para><b>This is the second sanctioned delete path against AuditLogs</b>, after retention
    /// (#86). See docs/audit-log.md — append-only here is a convention enforced by the absence of
    /// such paths, and <c>AuditLogAppendOnlyTests</c> names each exemption by file. A third needs the
    /// same kind of decision this one got.</para>
    /// </summary>
    public async Task<UserDeleteResult> DeleteAsync(int targetUserId, int actingUserId, CancellationToken cancellationToken)
    {
        if (targetUserId == actingUserId)
        {
            return UserDeleteResult.Refused(UserDeleteOutcome.CannotDeleteSelf);
        }

        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserDeleteResult.Refused(UserDeleteOutcome.NotFound);
        }

        // Mirrors Web's startup guard exactly: "can anyone sign in", not "does a user exist".
        if (user.PasswordHash is not null
            && !await dbContext.Users.AnyAsync(u => u.Id != targetUserId && u.PasswordHash != null, cancellationToken))
        {
            return UserDeleteResult.Refused(UserDeleteOutcome.LastSignInCapableAccount);
        }

        var blockers = await FindDeleteBlockersAsync(targetUserId, cancellationToken);
        if (blockers.Count > 0)
        {
            return new UserDeleteResult(UserDeleteOutcome.HasHistory, blockers);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var removedEmail = user.Email ?? "(no email)";

        // Memberships go first: UserTeam's FK is Restrict too, and it is not history — it is the
        // account's own configuration, meaningless once the account is gone.
        var memberships = await dbContext.UserTeams.Where(ut => ut.UserId == targetUserId).ToListAsync(cancellationToken);
        dbContext.UserTeams.RemoveRange(memberships);

        // The account's own lifecycle entries — rows ABOUT this user, whoever wrote them. Rows where
        // this user acted on anything else were already refused above, so nothing here erases a
        // record of what somebody did.
        var lifecycle = await dbContext.AuditLogs
            .Where(a => a.EntityType == nameof(User) && a.EntityId == targetUserId)
            .ToListAsync(cancellationToken);
        dbContext.AuditLogs.RemoveRange(lifecycle);

        // Written before the Identity delete so it shares the unit of work, and naming the email
        // because the id is about to stop resolving to anything.
        AddAudit(actingUserId, "UserDeleted", targetUserId,
            $"User {targetUserId} ({removedEmail}) permanently deleted; {lifecycle.Count} lifecycle audit entries removed with it.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Identity's own side — roles, logins, claims, tokens.
        var identityResult = await userManager.DeleteAsync(user);
        if (!identityResult.Succeeded)
        {
            return new UserDeleteResult(
                UserDeleteOutcome.HasHistory,
                [.. identityResult.Errors.Select(e => e.Description)]);
        }

        return UserDeleteResult.Deleted();
    }

    /// <summary>
    /// Every reference that makes an account history rather than a dead row, phrased for an admin.
    ///
    /// <para>Deliberately reports <b>all</b> of them rather than stopping at the first: an admin who
    /// clears one blocker only to be told about the next has learned nothing about whether the
    /// account is deletable at all.</para>
    ///
    /// <para><c>UserDeleteCoverageTests</c> fails the build if a foreign key to User is added without
    /// being accounted for here — the alternative being a delete that throws a <c>Restrict</c>
    /// violation at an admin instead of refusing politely.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> FindDeleteBlockersAsync(int userId, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();

        async Task CheckAsync(string noun, IQueryable<int> matching)
        {
            var count = await matching.CountAsync(cancellationToken);
            if (count > 0)
            {
                blockers.Add($"{count} {noun}{(count == 1 ? "" : "s")}");
            }
        }

        await CheckAsync("fee configuration created", dbContext.FeeConfigurations.Where(f => f.CreatedByUserId == userId).Select(f => f.Id));
        await CheckAsync("session marked complete", dbContext.Sessions.Where(s => s.TestingCompletedByUserId == userId).Select(s => s.Id));
        await CheckAsync("session submitted to a VEC", dbContext.Sessions.Where(s => s.VecSubmittedByUserId == userId).Select(s => s.Id));
        await CheckAsync("session filed with ARRL-VEC", dbContext.ArrlVecSubmissions.Where(a => a.SubmittedByUserId == userId).Select(a => a.Id));
        await CheckAsync("session fee override", dbContext.Sessions.Where(s => s.RetainedAmountOverrideByUserId == userId).Select(s => s.Id));
        await CheckAsync("candidate result recorded", dbContext.Candidates.Where(c => c.ResultMarkedByUserId == userId).Select(c => c.Id));
        await CheckAsync("payment refund flagged", dbContext.Payments.Where(p => p.RefundRequestedByUserId == userId).Select(p => p.Id));
        await CheckAsync("historical import requested", dbContext.HistoricalImportRequests.Where(h => h.RequestedByUserId == userId).Select(h => h.Id));
        await CheckAsync("team email setting edited", dbContext.EmailSettings.Where(e => e.UpdatedByUserId == userId).Select(e => e.Id));
        await CheckAsync("system setting edited", dbContext.SystemSettings.Where(x => x.UpdatedByUserId == userId).Select(x => x.Id));
        await CheckAsync("unmatched payment resolved", dbContext.UnmatchedSquarePayments.Where(u => u.ResolvedByUserId == userId).Select(u => u.Id));
        await CheckAsync("watched license added", dbContext.WatchedLicenses.Where(w => w.AddedByUserId == userId).Select(w => w.Id));
        // A refund is a financial record naming who authorized real money going back out (#375).
        // Of everything on this list it is the one least suitable for quiet removal.
        await CheckAsync("refund issued", dbContext.Refunds.Where(r => r.RequestedByUserId == userId).Select(r => r.Id));

        // Mail this account actually sent to candidates, in text it wrote itself (#144). Refused for
        // the same reason as the rest: the row records what a person did, and it is the only record
        // that a particular candidate was told a particular thing.
        await CheckAsync("email sent to a candidate", dbContext.CandidateEmailSends.Where(s => s.SentByUserId == userId).Select(s => s.Id));

        // Refused rather than nulled, which #188 left open. Nulling would quietly restructure other
        // people's accounts as a side effect of deleting this one; refusing makes the admin reassign
        // them deliberately, and the message says how many.
        await CheckAsync("user managed by this account", dbContext.Users.Where(u => u.ManagedByUserId == userId).Select(u => u.Id));

        // Actions this account took on ANYTHING ELSE. Its own lifecycle rows are excluded, because
        // those are what the delete removes; everything else records what somebody did.
        await CheckAsync("recorded action", dbContext.AuditLogs
            .Where(a => a.UserId == userId && !(a.EntityType == nameof(User) && a.EntityId == userId))
            .Select(a => a.Id));

        return blockers;
    }

    public async Task<UserActionResult> ReactivateAsync(int targetUserId, int actingUserId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString());
        if (user is null)
        {
            return UserActionResult.NotFound;
        }

        await userManager.SetLockoutEndDateAsync(user, null);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(actingUserId, "UserReactivated", user.Id, $"User {user.Id} reactivated.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return UserActionResult.Success;
    }

    private void AddAudit(int userId, string action, int entityId, string details, DateTime now) =>
        dbContext.AddAuditLog(userId, action, nameof(User), entityId, details, now);
}

public enum UserActionResult
{
    Success,
    NotFound,
    DuplicateEmail,
    InvalidPassword,
    CannotDeactivateSelf,
    InvalidManager,

    /// <summary>The account signs in through an external provider, so there is no local password to change.</summary>
    NoLocalPassword,

    /// <summary>Another login already claims that VolunteerExaminer record — two people cannot be the same examiner.</summary>
    VolunteerExaminerAlreadyLinked
}
