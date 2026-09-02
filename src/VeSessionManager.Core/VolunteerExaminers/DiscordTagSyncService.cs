using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Discord;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Works out what a team's VE tags would become if Discord's roles were applied to them, and applies
/// it (#519). <see cref="BuildPreviewAsync"/> writes nothing at all; <see cref="ApplyAsync"/> writes
/// tag assignments and matched account ids to <i>this app's</i> database only.
///
/// <para><b>Nothing here ever writes to Discord</b> — no role granted or revoked, no nickname changed,
/// no permission touched. Roles are managed in Discord and read from it; see
/// <see cref="IDiscordGuildClient"/>.</para>
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
    /// Writes the plan — the only method here that changes anything, and still only in this app's own
    /// database. Nothing is ever written to Discord.
    ///
    /// <para><b>The plan is rebuilt from Discord rather than replayed.</b> A preview is a photograph,
    /// and a role revoked in the seconds between looking and clicking would otherwise be applied as
    /// though it were still held. <paramref name="previewedFingerprint"/> is compared against the
    /// fresh plan purely to report that the picture was out of date — it never blocks the write, since
    /// the fresh answer is the correct one either way and refusing it would just mean looking at the
    /// same screen again.</para>
    ///
    /// <para>Everything in the plan is applied together. There is no per-row selection: the preview is
    /// where a wrong row is caught, and the fix for one is in Discord or in the tag map, not in
    /// skipping it here — a skip that is not remembered would silently return on the next run.</para>
    /// </summary>
    /// <param name="previewedFingerprint">The <see cref="DiscordTagSyncPlan.Fingerprint"/> of what the user was shown, or null when nothing was previewed (a scheduled run).</param>
    public async Task<DiscordTagSyncApplyResult> ApplyAsync(
        int teamId, int userId, string? previewedFingerprint, CancellationToken cancellationToken)
    {
        var plan = await BuildPreviewAsync(teamId, cancellationToken);
        if (!plan.Ran)
        {
            // Skipped covers every "no data" case, and no data must never be written as "nobody holds
            // a role" — which under the rule would strip every mapped tag on the team.
            return new DiscordTagSyncApplyResult(plan, DifferedFromPreview: false, 0, 0, 0);
        }

        var differed = previewedFingerprint is not null
            && !string.Equals(previewedFingerprint, plan.Fingerprint, StringComparison.Ordinal);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var added = 0;
        var removed = 0;

        foreach (var change in plan.Changes)
        {
            foreach (var tag in change.TagsToAdd)
            {
                dbContext.VeTagAssignments.Add(new VeTagAssignment
                {
                    VeTeamMembershipId = change.VeTeamMembershipId,
                    VeTagId = tag.TagId,
                    CreatedUtc = now,
                });
                added++;
            }

            if (change.TagsToRemove.Count > 0)
            {
                var removing = change.TagsToRemove.Select(t => t.TagId).ToList();
                var assignments = await dbContext.VeTagAssignments
                    .Where(a => a.VeTeamMembershipId == change.VeTeamMembershipId && removing.Contains(a.VeTagId))
                    .ToListAsync(cancellationToken);
                dbContext.VeTagAssignments.RemoveRange(assignments);
                removed += assignments.Count;
            }

            var description = Describe(change);
            dbContext.AddAuditLog(userId, "VeTagsUpdatedFromDiscord", nameof(VeTeamMembership), change.VeTeamMembershipId,
                $"{change.CallSign ?? change.Name}: {description} (from Discord role membership).", now);
        }

        var linked = 0;
        foreach (var link in plan.NewLinks)
        {
            var person = await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.Id == link.VolunteerExaminerId, cancellationToken);
            if (person is null)
            {
                continue;
            }

            person.DiscordUserId = link.DiscordUserId;

            // The username follows the id rather than the other way round: it is the label on a link
            // that is now established, and leaving a hand-typed guess beside a confirmed account would
            // make the screen disagree with itself.
            person.DiscordUsername = link.DiscordUsername;
            person.UpdatedUtc = now;
            linked++;

            dbContext.AddAuditLog(userId, "VeDiscordAccountLinked", nameof(VolunteerExaminer), person.Id,
                $"{link.CallSign ?? link.Name} matched to Discord account {link.DiscordUsername}.", now);
        }

        // One transaction: this is a handful of rows from a single button press, and a half-applied
        // roster is harder to reason about than one that either happened or did not. That is a
        // different situation from the scan-based jobs, which save per item because they run unattended
        // across hundreds of rows and must never lose progress already made.
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Applied Discord tag sync for team {TeamId}: {Added} tag(s) added, {Removed} removed, {Linked} account(s) matched",
            teamId, added, removed, linked);

        return new DiscordTagSyncApplyResult(plan, differed, added, removed, linked);
    }

    private static string Describe(DiscordTagChange change)
    {
        var parts = new List<string>();
        if (change.TagsToAdd.Count > 0)
        {
            parts.Add("added " + string.Join(", ", change.TagsToAdd.Select(t => $"'{t.Name}'")));
        }

        if (change.TagsToRemove.Count > 0)
        {
            parts.Add("removed " + string.Join(", ", change.TagsToRemove.Select(t => $"'{t.Name}'")));
        }

        return string.Join("; ", parts);
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

    /// <summary>
    /// A stable summary of everything this plan would write, for comparing a preview against the fresh
    /// plan built at apply time. Order-independent by construction, since the change list follows
    /// Discord's member order and that is not guaranteed stable between calls.
    ///
    /// <para>Deliberately covers only the writes — the exception lists can shift (somebody joins the
    /// server, somebody fixes their nickname) without the outcome of applying changing at all, and
    /// reporting that as "this differs from what you saw" would cry wolf.</para>
    /// </summary>
    public string Fingerprint =>
        string.Join("|", Changes
            .Select(c => $"{c.VeTeamMembershipId}:{c.DiscordUserId}"
                + $":+{string.Join(",", c.TagsToAdd.Select(t => t.TagId).Order())}"
                + $":-{string.Join(",", c.TagsToRemove.Select(t => t.TagId).Order())}")
            .Concat(NewLinks.Select(l => $"L{l.VolunteerExaminerId}:{l.DiscordUserId}"))
            .Order(StringComparer.Ordinal));

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

/// <param name="Plan">The plan that was actually applied — freshly built, not the one previewed.</param>
/// <param name="DifferedFromPreview">True when Discord changed between the preview and this write. Reported, never a refusal: the fresh answer is the correct one either way.</param>
public record DiscordTagSyncApplyResult(
    DiscordTagSyncPlan Plan,
    bool DifferedFromPreview,
    int TagsAdded,
    int TagsRemoved,
    int Linked);

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
