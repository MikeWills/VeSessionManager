using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// Turns a rule's <see cref="MessageRecipient"/> into the email addresses it actually means.
///
/// <para>Implements the recipient axis of <c>docs/trigger-recipient-matrix.md</c>: every trigger
/// point should be able to send to a candidate, a VE (over Discord), the session lead, or an admin
/// role — rather than a trigger owning its recipients, which is what
/// <c>MessageTriggerDefinitions.LegalRecipients</c> did.</para>
///
/// <para><b>Returns a list, and that is the change.</b> The old <c>ResolveAddress</c> answered one
/// address per subject, which cannot express "every team admin". A role recipient is inherently
/// several people, so the send loop fans out over the result.</para>
///
/// <para>⚠️ <b>The session lead and the role recipients are different populations from different
/// systems, and must not be merged.</b> The lead comes from ExamTools —
/// <c>Session.TeamLeadCallSign</c> → VE record → that VE's email — and <b>may have no app account at
/// all</b>, since a VE leading a session is not required to be a user here. The role recipients come
/// from Identity, and those users need not be VEs. Mike's *"Team Lead = SM"* is true of the people
/// and false of the plumbing.</para>
/// </summary>
public static class MessageRecipientResolver
{
    /// <summary>
    /// The addresses a rule's recipient resolves to for one subject. Empty means nobody — which the
    /// caller records as <c>NoRecipient</c> rather than treating as a failure.
    /// </summary>
    /// <param name="team">Scopes the role lookups. A rule belongs to one team and must never resolve another team's staff.</param>
    /// <param name="sessionLeadCallSign">From the subject's session, as ExamTools reported it — placeholders included.</param>
    /// <param name="teamAdminAddress">The configured notification address, for the pre-existing <see cref="MessageRecipient.TeamAdminAddress"/>.</param>
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        AppDbContext dbContext,
        Team team,
        MessageRecipient recipient,
        string? sessionLeadCallSign,
        string? candidateEmail,
        string? teamAdminAddress,
        CancellationToken cancellationToken)
    {
        var addresses = recipient switch
        {
            MessageRecipient.Candidate => One(candidateEmail),
            MessageRecipient.TeamAdminAddress => One(teamAdminAddress),
            MessageRecipient.SessionLead => await ResolveSessionLeadAsync(dbContext, sessionLeadCallSign, cancellationToken),
            MessageRecipient.TeamAdmins => await ResolveRoleAsync(dbContext, team, UserRole.TeamAdmin, teamScoped: true, cancellationToken),
            MessageRecipient.SessionManagers => await ResolveRoleAsync(dbContext, team, UserRole.SessionManager, teamScoped: true, cancellationToken),

            // Not team-scoped, deliberately: a SystemAdmin spans every team by definition, and
            // AdminAccessScope already treats them that way (GetEffectiveTeamIds returns null for
            // them, meaning "all teams" rather than "none"). Requiring a UserTeam row would resolve
            // to nobody for the one role that is always entitled to know.
            MessageRecipient.SystemAdmins => await ResolveRoleAsync(dbContext, team, UserRole.SystemAdmin, teamScoped: false, cancellationToken),

            // A channel post is not an address and must never become one — the Discord path builds no
            // EmailMessage at all, which is what keeps the From, Reply-To, monitoring Bcc and
            // unsubscribe footer structurally unable to reach a room full of people.
            MessageRecipient.DiscordChannel => [],
            _ => []
        };

        // Collapsed case-insensitively: two roles, or two accounts, resolving to one person should
        // send one message rather than two identical ones.
        return addresses
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The same lookup the Reply-To feature already performs, reused rather than reinvented.
    ///
    /// <para>⚠️ <c>CallSign.Normalize</c> is load-bearing: ExamTools puts a literal
    /// <c>&lt;UNKNOWN&gt;</c> in this field, and looking one up once fused two people into a single VE
    /// record. A lead that cannot be identified resolves to nobody.</para>
    ///
    /// <para>A lead with a VE record but no email also resolves to nobody, <b>not</b> to the team
    /// address. Reply-To can fall back to the team because a reply going somewhere sensible is better
    /// than bouncing; a To line cannot, because quietly delivering a message to a different person
    /// than the rule names is worse than not sending it.</para>
    /// </summary>
    private static async Task<List<string?>> ResolveSessionLeadAsync(
        AppDbContext dbContext, string? sessionLeadCallSign, CancellationToken cancellationToken)
    {
        if (CallSign.Normalize(sessionLeadCallSign) is not { } callSign)
        {
            return [];
        }

        var email = await dbContext.VolunteerExaminers
            .Where(v => v.CallSign == callSign)
            .Select(v => v.Email)
            .FirstOrDefaultAsync(cancellationToken);

        return One(email);
    }

    /// <summary>
    /// Everyone holding a role, as app users rather than VEs.
    ///
    /// <para>No unsubscribe surface and no CAN-SPAM footer: these are staff being told about their
    /// own team's work, not marketing to a member of the public. That is also why the VE unsubscribe
    /// (#191) does not apply — the engine sends VEs no email at all, by decision; VEs are reached over
    /// Discord.</para>
    /// </summary>
    private static async Task<List<string?>> ResolveRoleAsync(
        AppDbContext dbContext, Team team, UserRole role, bool teamScoped, CancellationToken cancellationToken)
    {
        var users = dbContext.Users.Where(u => u.Role == role);

        if (teamScoped)
        {
            users = users.Where(u => u.UserTeams.Any(ut => ut.TeamId == team.Id));
        }

        return await users.Select(u => u.Email).ToListAsync(cancellationToken);
    }

    private static List<string?> One(string? address) => string.IsNullOrWhiteSpace(address) ? [] : [address];
}
