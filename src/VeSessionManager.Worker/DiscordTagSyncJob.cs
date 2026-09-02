using VeSessionManager.Core.Entities;
using VeSessionManager.Core.Jobs;
using VeSessionManager.Core.VolunteerExaminers;

namespace VeSessionManager.Worker;

/// <summary>
/// Applies each opted-in team's Discord roles to its VE tags, daily (#519 step 4) — the unattended
/// form of the button on the Discord Tags screen, running the identical
/// <see cref="DiscordTagSyncService.ApplyAsync"/>.
///
/// <para><b>Off unless a team turned it on</b> (<see cref="Team.DiscordTagSyncEnabled"/>). This job
/// removes tags as well as adding them, so a VE whose Discord display name stops carrying their call
/// sign loses a mapped tag with nobody watching; the on-demand check is how a team learns whether the
/// matching works against their server, and the switch is how they say it does. A team that has mapped
/// tags but not flipped it is skipped silently — that is a team using the check, not a
/// misconfiguration.</para>
///
/// <para><b>Safe to run repeatedly by construction</b>, which is what makes a schedule reasonable at
/// all: the plan is a diff against current state, so a second run proposes nothing. The same property
/// makes a missed tick harmless — the next one catches up — which is the assumption the shared 24-hour
/// timer idiom rests on, and it holds here because Discord's roles are current state rather than a
/// one-shot window. (Contrast <c>FccDailyWatcherJob</c>, where it does not.)</para>
/// </summary>
public class DiscordTagSyncJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DiscordTagSyncJob> logger)
    : PerTeamDailyJob(scopeFactory, configuration, logger, JobSchedules.DiscordTagSync)
{
    protected override async Task<object?> RunForTeamAsync(IServiceProvider scopedServices, Team team, CancellationToken cancellationToken)
    {
        if (!team.DiscordTagSyncEnabled)
        {
            return "Not turned on for this team.";
        }

        var syncService = scopedServices.GetRequiredService<DiscordTagSyncService>();

        // No previewed fingerprint: nobody looked at a screen, so there is nothing for the fresh plan
        // to disagree with. ApplyAsync treats null as "no claim about staleness" rather than as
        // "everything differed", which is exactly this case.
        // Null user: nobody clicked. The audit log already uses null for "not a person's action", and
        // naming a real admin would put their name on a change they did not make.
        var result = await syncService.ApplyAsync(team.Id, userId: null, previewedFingerprint: null, cancellationToken);

        if (!result.Plan.Ran)
        {
            // Includes the case this job most needs to survive: Discord unreachable, or the member
            // list coming back empty because the privileged intent is off. Nothing was written, and
            // the reason is worth showing on the Job Run History page rather than a bare "0 changes".
            return $"Skipped — {result.Plan.SkippedReason}";
        }

        var exceptions = result.Plan.MembersWithoutVolunteerExaminer.Count
            + result.Plan.VolunteerExaminersWithoutMember.Count
            + result.Plan.AmbiguousMembers.Count;

        return $"{result.TagsAdded} tag(s) added, {result.TagsRemoved} removed, "
            + $"{result.Linked} Discord account(s) matched, {exceptions} exception(s).";
    }
}
