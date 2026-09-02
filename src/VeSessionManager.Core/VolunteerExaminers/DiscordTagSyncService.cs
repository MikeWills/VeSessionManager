using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Works out what a team's VE tags would become if Discord's roles were applied to them (#519 step 2).
/// <b>Read-only — this builds a plan and writes nothing</b>, neither to the database nor to Discord.
///
/// <para>The decided rule, in full, is in docs/discord-tag-sync.md. In short: for a VE matched to a
/// member of the team's server, and only for tags carrying a <see cref="VeTag.DiscordRoleId"/>,
/// holding the role means holding the tag and not holding the role means not holding it. Everyone and
/// everything else is left alone — an unmatched VE keeps every tag, an unmatched member is never given
/// a VE record, and an unmapped tag is never read or written.</para>
/// </summary>
public class DiscordTagSyncService(
    AppDbContext dbContext,
    IDiscordGuildClient discordGuildClient,
    TimeProvider timeProvider,
    ILogger<DiscordTagSyncService> logger)
{
    public async Task<DiscordTagSyncPlan> BuildPreviewAsync(int teamId, CancellationToken cancellationToken)
    {
        var team = await dbContext.Teams.FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
        {
            return DiscordTagSyncPlan.Skipped("That team no longer exists.");
        }

        if (!discordGuildClient.IsConfigured)
        {
            return DiscordTagSyncPlan.Skipped("Discord isn't set up for this deployment.");
        }

        if (team.DiscordGuildId is not { } guildId || guildId == 0)
        {
            return DiscordTagSyncPlan.Skipped("This team has no Discord server set.");
        }

        // Only mapped tags are in play, and if none are, this is a team that has not opted in — not a
        // failure, and it must not read as one.
        var mappedTags = await dbContext.VeTags
            .Where(t => t.TeamId == teamId && t.DiscordRoleId != null)
            .ToListAsync(cancellationToken);
        if (mappedTags.Count == 0)
        {
            return DiscordTagSyncPlan.Skipped("No tags on this team are matched to a Discord role yet.");
        }

        IReadOnlyList<DiscordGuildMember> members;
        try
        {
            members = await discordGuildClient.ListMembersAsync(guildId, cancellationToken);
        }
        catch (Exception ex)
        {
            // No data is not "nobody holds a role". Refusing the whole run is the only safe reading:
            // under the rule above, an absent role removes a tag, so a failed fetch applied literally
            // would strip every mapped tag from every matched VE on the team.
            logger.LogWarning(ex, "Could not read Discord members for team {TeamId} — no tag changes proposed", teamId);
            return DiscordTagSyncPlan.Skipped("Couldn't read the member list from Discord. Nothing was changed.");
        }

        if (members.Count == 0)
        {
            // Same conclusion, different shape. A guild always contains at least the bot, so empty is
            // "could not read" — most often the GUILD_MEMBERS privileged intent being switched off,
            // which Discord answers with an empty list rather than an error.
            logger.LogWarning(
                "Discord returned no members for team {TeamId} (guild {GuildId}) — treating as no data, not as an empty server",
                teamId, guildId);
            return DiscordTagSyncPlan.Skipped(
                "Discord returned no members. The bot most likely needs the Server Members Intent turned on. Nothing was changed.");
        }

        var memberships = await dbContext.VeTeamMemberships
            .Where(m => m.TeamId == teamId && m.IsActive)
            .Include(m => m.VolunteerExaminer)
            .Include(m => m.TagAssignments)
            .ToListAsync(cancellationToken);

        var index = await BuildIndexAsync(memberships, cancellationToken);
        var mappedRoleIds = mappedTags.Select(t => t.DiscordRoleId!.Value).ToHashSet();

        var changes = new List<DiscordTagChange>();
        var membersWithoutVe = new List<DiscordMemberSummary>();
        var links = new List<DiscordIdentityLink>();
        var ambiguous = new List<DiscordAmbiguousMember>();
        var matchedMembershipIds = new HashSet<int>();

        foreach (var member in members)
        {
            var matches = index.Resolve(member);
            if (matches.Count > 1)
            {
                // Two VEs named in one display name. Nothing is guessed and nothing changes — taking
                // the first would assign one person's tags to another by string order.
                ambiguous.Add(new DiscordAmbiguousMember(
                    member.Id, member.DisplayName,
                    [.. matches.Select(m => m.VolunteerExaminer.CallSign ?? m.VolunteerExaminer.Name).Order()]));
                continue;
            }

            if (matches.Count == 0)
            {
                // Reported only when they hold a mapped role — i.e. only when this would have changed
                // something had they matched. A team's server is mostly candidates and club members,
                // and a list nobody can read is one nobody reads.
                if (member.RoleIds.Any(mappedRoleIds.Contains))
                {
                    membersWithoutVe.Add(new DiscordMemberSummary(member.Id, member.DisplayName, member.Username));
                }

                continue;
            }

            var membership = matches[0];
            matchedMembershipIds.Add(membership.Id);

            var held = member.RoleIds.ToHashSet();
            var add = mappedTags
                .Where(t => held.Contains(t.DiscordRoleId!.Value) && membership.TagAssignments.All(a => a.VeTagId != t.Id))
                .Select(t => new DiscordTagRef(t.Id, t.Name))
                .ToList();
            var remove = mappedTags
                .Where(t => !held.Contains(t.DiscordRoleId!.Value) && membership.TagAssignments.Any(a => a.VeTagId == t.Id))
                .Select(t => new DiscordTagRef(t.Id, t.Name))
                .ToList();

            // Learning who somebody is on Discord is worth storing but is not a tag change, and
            // listing it as one would bury the changes that are. Tracked separately so apply can write
            // it and the screen can count it in a sentence.
            if (membership.VolunteerExaminer.DiscordUserId != member.Id)
            {
                links.Add(new DiscordIdentityLink(
                    membership.VolunteerExaminerId,
                    membership.VolunteerExaminer.Name,
                    membership.VolunteerExaminer.CallSign,
                    member.Id,
                    member.Username,
                    member.DisplayName));
            }

            // A match with nothing to change still counts as matched — that is what keeps them off the
            // "not in Discord" list below.
            if (add.Count > 0 || remove.Count > 0)
            {
                changes.Add(new DiscordTagChange(
                    membership.Id,
                    membership.VolunteerExaminerId,
                    membership.VolunteerExaminer.Name,
                    membership.VolunteerExaminer.CallSign,
                    member.Id,
                    member.DisplayName,
                    add,
                    remove));
            }
        }

        var vesWithoutMember = memberships
            .Where(m => !matchedMembershipIds.Contains(m.Id))
            .Select(m => new DiscordUnmatchedVe(m.VolunteerExaminerId, m.VolunteerExaminer.Name, m.VolunteerExaminer.CallSign))
            .OrderBy(v => v.CallSign ?? v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DiscordTagSyncPlan(
            Ran: true,
            SkippedReason: null,
            BuiltUtc: timeProvider.GetUtcNow().UtcDateTime,
            Changes: changes,
            NewLinks: links,
            MembersWithoutVolunteerExaminer: membersWithoutVe,
            VolunteerExaminersWithoutMember: vesWithoutMember,
            AmbiguousMembers: ambiguous);
    }

    /// <summary>
    /// The lookup from a guild member to this team's memberships, in the order identity is trusted:
    /// a stored Discord id, then the hand-entered username, then a call sign in the display name —
    /// current or former.
    /// </summary>
    private async Task<MemberIndex> BuildIndexAsync(List<VeTeamMembership> memberships, CancellationToken cancellationToken)
    {
        var veIds = memberships.Select(m => m.VolunteerExaminerId).ToList();
        var formerCallSigns = await dbContext.VeCallSignHistories
            .Where(h => veIds.Contains(h.VolunteerExaminerId))
            .Select(h => new { h.VolunteerExaminerId, h.CallSign })
            .ToListAsync(cancellationToken);

        var byCallSign = new Dictionary<string, List<VeTeamMembership>>(StringComparer.Ordinal);
        void Register(string? callSign, VeTeamMembership membership)
        {
            var normalized = CallSign.Normalize(callSign);
            if (normalized is null)
            {
                return;
            }

            if (!byCallSign.TryGetValue(normalized, out var list))
            {
                byCallSign[normalized] = list = [];
            }

            if (list.All(m => m.Id != membership.Id))
            {
                list.Add(membership);
            }
        }

        foreach (var membership in memberships)
        {
            Register(membership.VolunteerExaminer.CallSign, membership);

            // A vanity call comes through and a server nickname lags for months. The person is the
            // same person, which is what VeCallSignHistory exists to say.
            foreach (var former in formerCallSigns.Where(f => f.VolunteerExaminerId == membership.VolunteerExaminerId))
            {
                Register(former.CallSign, membership);
            }
        }

        return new MemberIndex(memberships, byCallSign);
    }

    private sealed class MemberIndex(List<VeTeamMembership> memberships, Dictionary<string, List<VeTeamMembership>> byCallSign)
    {
        public List<VeTeamMembership> Resolve(DiscordGuildMember member)
        {
            // A stored id is the identity and ends the question — including for someone who has since
            // dropped their call sign from their nickname, which is exactly when guessing would fail.
            var byId = memberships.Where(m => m.VolunteerExaminer.DiscordUserId == member.Id).ToList();
            if (byId.Count > 0)
            {
                return byId;
            }

            // Hand-entered by an admin (or the VE) long before this feature existed. Trusted above the
            // display name because a person typed it deliberately.
            var byUsername = memberships
                .Where(m => !string.IsNullOrWhiteSpace(m.VolunteerExaminer.DiscordUsername)
                    && string.Equals(m.VolunteerExaminer.DiscordUsername!.Trim().TrimStart('@'), member.Username, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byUsername.Count > 0)
            {
                return byUsername;
            }

            var matched = new List<VeTeamMembership>();
            foreach (var candidate in DiscordCallSignParser.Candidates(member.DisplayName))
            {
                if (!byCallSign.TryGetValue(candidate, out var found))
                {
                    continue;
                }

                foreach (var membership in found.Where(membership => matched.All(m => m.Id != membership.Id)))
                {
                    matched.Add(membership);
                }
            }

            return matched;
        }
    }
}

/// <param name="Ran">False when nothing was evaluated at all. <b>Not the same as "no changes"</b> — a run that read the roster and found it already correct has <c>Ran = true</c> and an empty <see cref="Changes"/>.</param>
/// <param name="SkippedReason">Why it did not run, in words a Session Manager can act on. Null when it ran.</param>
public record DiscordTagSyncPlan(
    bool Ran,
    string? SkippedReason,
    DateTime? BuiltUtc,
    IReadOnlyList<DiscordTagChange> Changes,
    IReadOnlyList<DiscordIdentityLink> NewLinks,
    IReadOnlyList<DiscordMemberSummary> MembersWithoutVolunteerExaminer,
    IReadOnlyList<DiscordUnmatchedVe> VolunteerExaminersWithoutMember,
    IReadOnlyList<DiscordAmbiguousMember> AmbiguousMembers)
{
    public static DiscordTagSyncPlan Skipped(string reason) => new(false, reason, null, [], [], [], [], []);

    public bool HasAnythingToShow =>
        Changes.Count > 0 || NewLinks.Count > 0 || MembersWithoutVolunteerExaminer.Count > 0
        || VolunteerExaminersWithoutMember.Count > 0 || AmbiguousMembers.Count > 0;
}

/// <param name="DiscordUserId">Stored on apply, so the next run matches this person by id rather than by their display name.</param>
public record DiscordTagChange(
    int VeTeamMembershipId,
    int VolunteerExaminerId,
    string Name,
    string? CallSign,
    ulong DiscordUserId,
    string DiscordDisplayName,
    IReadOnlyList<DiscordTagRef> TagsToAdd,
    IReadOnlyList<DiscordTagRef> TagsToRemove);

public record DiscordTagRef(int TagId, string Name);

public record DiscordMemberSummary(ulong DiscordUserId, string DisplayName, string Username);

/// <summary>
/// A VE this run recognised on Discord whose <see cref="VolunteerExaminer.DiscordUserId"/> is not yet
/// stored (or has changed). Applying it is what stops the next run from having to guess from a display
/// name — and what keeps the match when somebody drops their call sign from their nickname.
/// </summary>
public record DiscordIdentityLink(
    int VolunteerExaminerId,
    string Name,
    string? CallSign,
    ulong DiscordUserId,
    string DiscordUsername,
    string DiscordDisplayName);

public record DiscordUnmatchedVe(int VolunteerExaminerId, string Name, string? CallSign);

/// <param name="MatchedCallSigns">Who the name could have meant — shown so a human can settle it rather than being told only that it was unclear.</param>
public record DiscordAmbiguousMember(ulong DiscordUserId, string DisplayName, IReadOnlyList<string> MatchedCallSigns);
