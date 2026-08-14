using Microsoft.AspNetCore.Identity;

namespace VeSessionManager.Core.Entities;

/// <summary>
/// The ASP.NET Core Identity user (Phase 9a) — inherits UserName/Email (now nullable, superseding
/// the old required string Email)/PasswordHash/SecurityStamp/etc. from IdentityUser&lt;int&gt;.
/// Deliberately does NOT use Identity's own Role tables (AspNetRoles/UserRoles) — Role stays one
/// plain enum column, matching every other "pick one of N" field in this codebase (see
/// docs/admin-auth.md).
/// </summary>
public class User : IdentityUser<int>
{
    public required string Name { get; set; }

    /// <summary>
    /// The account holder's amateur call sign (requested 2026-07-30). Nullable — every existing
    /// account predates this field, and a Session Manager isn't required to be licensed for the app
    /// to work.
    ///
    /// <para>Stored upper-invariant, matching <see cref="VolunteerExaminer.CallSign"/>'s existing
    /// convention — normalize on write (see UserManagementService) so the two are comparable. This is
    /// deliberately *not* a foreign key to VolunteerExaminer: a VE row is team-scoped and synced from
    /// ExamTools, whereas a User is a login that may belong to several teams or none, so the same
    /// person can legitimately be one User and several VE rows. Matching them up is a separate
    /// question from recording the call sign here.</para>
    /// </summary>
    public string? CallSign { get; set; }

    /// <summary>
    /// When this user last asked for a password reset email, used purely to throttle repeats (see
    /// PasswordResetService.RequestThrottle). Stamped before the send so a failing SMTP server can't
    /// be turned into a mail-bombing loop; cleared on a successful reset. Null = never requested.
    /// </summary>
    public DateTime? LastPasswordResetRequestedUtc { get; set; }

    public UserRole Role { get; set; }

    /// <summary>
    /// Light or dark, remembered on the account rather than in one browser's localStorage. Defaults
    /// to <see cref="ThemePreference.System"/> — see that enum for why there is no way back to it
    /// once a choice is made.
    ///
    /// <para>Written by the chassis theme toggle through <c>/Account/Theme</c>. localStorage is still
    /// kept in step client-side, because it is the only home a signed-out page has (login, the
    /// privacy page, VE self-service) and because it lets the first paint of the *next* page happen
    /// before this value is known.</para>
    /// </summary>
    public ThemePreference ThemePreference { get; set; }

    /// <summary>
    /// Set when an admin hands out a password the user did not choose, and cleared the moment they
    /// change it. While true, every authenticated request is redirected to Change password
    /// (RequirePasswordChangeMiddleware).
    ///
    /// <para>An admin-created account starts with a password its owner did not pick and that at
    /// least one other person knows — often typed into a chat message. Nothing previously nudged
    /// anyone to replace it, and until 2026-08-07 there was no self-service way to do so at all:
    /// only the emailed reset flow, on a deployment where system SMTP has never been configured.</para>
    ///
    /// <para>Never set for an OAuth account — their provider owns the credential and there is no
    /// local password to change.</para>
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// Replaces the old single, nullable TeamId (Phase 9a) — a TeamAdmin/SessionManager can now
    /// belong to more than one Team (issue #19). Empty for SystemAdmin (deployment-wide) and for
    /// TeamLead, whose effective teams are resolved transitively through ManagedByUser instead (see
    /// SessionAccessScope.GetEffectiveTeamIds).
    /// </summary>
    public List<UserTeam> UserTeams { get; } = [];

    /// <summary>TeamLead's assigned manager (a SessionManager or TeamAdmin) — role-agnostic, see SessionAccessScope.</summary>
    public int? ManagedByUserId { get; set; }
    public User? ManagedByUser { get; set; }

    /// <summary>
    /// The <see cref="VolunteerExaminer"/> record describing this same human, when there is one
    /// (#224). A login and a VE record are two views of one person, and until this existed nothing
    /// connected them — the same someone appeared twice, with two email addresses free to diverge.
    ///
    /// <para><b>Identity, never authorisation.</b> Linking records who someone is. It grants nothing
    /// and revokes nothing: roles still come from <see cref="Role"/> and teams from
    /// <see cref="UserTeams"/>. VE self-service remains a separate authentication scheme with its own
    /// cookie and its own barriers (docs/ve-self-service.md) — being linked does not let a VE sign
    /// into the admin app, and being an admin does not sign anyone into self-service.</para>
    ///
    /// <para><b>Nullable both ways round, on purpose.</b> Most VEs have no login at all — 176 of them
    /// against a handful of users — and some users are not VEs, such as a treasurer who administers
    /// without being accredited. The link records the overlap without pretending the two populations
    /// are the same.</para>
    ///
    /// <para>At most one login per VE record, enforced by a filtered unique index — two people
    /// claiming to be the same examiner is a data error, not a state to support.</para>
    /// </summary>
    public int? VolunteerExaminerId { get; set; }
    public VolunteerExaminer? VolunteerExaminer { get; set; }
}
