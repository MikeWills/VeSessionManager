using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Ingestion;

/// <summary>
/// Issue #73: answers "when was this team last polled, when is it next due, and is the Worker even
/// alive?" — none of which was answerable from the UI before, because both halves of the schedule
/// live in places nobody can see. Answering it once took a direct database query.
///
/// Two independent schedules feed the answer, which is exactly why it is non-obvious:
/// SessionIngestionJob ticks every Jobs:SessionIngestionIntervalSeconds (default 300s), and each
/// team is then separately gated by <see cref="IngestionScheduleService.IsDue"/> against
/// SystemSettings.SessionIngestionIntervalMinutes (default 60). So the job "runs" every 5 minutes
/// while a given team is only polled hourly — and **a skipped team writes no JobRunHistory row at
/// all**, which is why the ops dashboard's silence is indistinguishable from a dead Worker.
///
/// Everything here is derived from data already stored (Team.LastIngestionRunUtc + the one
/// SystemSettings row); no new schema. The "is it due" arithmetic deliberately delegates to
/// IngestionScheduleService rather than restating it, so the countdown can never disagree with the
/// gate it is describing.
/// </summary>
public class IngestionStatusService(
    AppDbContext dbContext,
    IngestionScheduleService scheduleService,
    TimeProvider timeProvider)
{
    /// <summary>
    /// How many polling intervals may elapse with *no* team polled before the Worker is presumed
    /// down. Two rather than one: a single missed interval is normal jitter (the job's own 5-minute
    /// tick doesn't align with the hourly per-team gate, so the real gap between polls is routinely
    /// a little over the interval), whereas two consecutive misses cannot happen while the Worker is
    /// running and healthy.
    /// </summary>
    public const int StaleIntervalMultiplier = 2;

    public async Task<IngestionStatusReport> GetAsync(IReadOnlyList<int>? teamIds, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Read directly rather than through SystemSettingsService.GetAsync, which get-or-creates the
        // singleton row — that is a *write*, and this service is called from page renders (including
        // the site-wide health banner on every request). Same read-only reasoning, and the same
        // fallback-to-the-seeded-default idiom, as _TestModeBanner.cshtml.
        var intervalMinutes = await dbContext.SystemSettings
            .Where(s => s.Id == SystemSettingsService.SingletonId)
            .Select(s => (int?)s.SessionIngestionIntervalMinutes)
            .FirstOrDefaultAsync(cancellationToken)
            ?? SystemSettingsService.DefaultSessionIngestionIntervalMinutes;

        // Health is deliberately evaluated across EVERY team, not just the ones this user can see:
        // "is the Worker alive" is a deployment-wide question, and a TeamAdmin viewing one team must
        // not be told the Worker is down because *their* team happens to be newly created. The
        // per-team rows below are the part that gets scoped.
        var newestAcrossAllTeams = await dbContext.Teams.MaxAsync(t => (DateTime?)t.LastIngestionRunUtc, cancellationToken);
        var anyTeams = await dbContext.Teams.AnyAsync(cancellationToken);

        var query = dbContext.Teams.AsQueryable();
        if (teamIds is not null)
        {
            query = query.Where(t => teamIds.Contains(t.Id));
        }

        var teams = await query.OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var rows = teams.Select(t => BuildRow(t, intervalMinutes, now)).ToList();

        var staleAfter = TimeSpan.FromMinutes(intervalMinutes * StaleIntervalMultiplier);
        var health = !anyTeams ? IngestionHealthState.NoTeams
            : newestAcrossAllTeams is null ? IngestionHealthState.NeverPolled
            : now - newestAcrossAllTeams.Value > staleAfter ? IngestionHealthState.Stale
            : IngestionHealthState.Healthy;

        return new IngestionStatusReport(rows, intervalMinutes, newestAcrossAllTeams, health, staleAfter, now);
    }

    private TeamIngestionStatus BuildRow(Team team, int intervalMinutes, DateTime now)
    {
        var isDue = scheduleService.IsDue(team, intervalMinutes, now);
        // Null LastIngestionRunUtc means "never run, always due" per IngestionScheduleService, so
        // there is no meaningful next-due instant to show — the row reads "due now" instead.
        var nextDueUtc = team.LastIngestionRunUtc?.AddMinutes(intervalMinutes);
        return new TeamIngestionStatus(
            team.Id, team.Name, team.LastIngestionRunUtc, nextDueUtc, isDue, team.IsExamToolsConfigured);
    }
}

public record TeamIngestionStatus(
    int TeamId,
    string TeamName,
    DateTime? LastRunUtc,
    DateTime? NextDueUtc,
    bool IsDueNow,
    bool IsExamToolsConfigured);

/// <summary>
/// Four distinct states rather than a bare "stale" bool, because the fixes differ and a fresh
/// deployment must not show a red "the Worker is down" alarm that is really "you haven't started it
/// yet, and there is nothing for it to do."
/// </summary>
public enum IngestionHealthState
{
    /// <summary>At least one team was polled within the staleness window. Nothing to report.</summary>
    Healthy,

    /// <summary>Teams exist but not one has ever been polled — a Worker that has never run, or has never had working credentials.</summary>
    NeverPolled,

    /// <summary>Teams have been polled before, but not recently enough. This is the "the Worker is down or stuck" signal.</summary>
    Stale,

    /// <summary>No teams configured at all, so there is nothing to poll and nothing to warn about.</summary>
    NoTeams
}

public record IngestionStatusReport(
    IReadOnlyList<TeamIngestionStatus> Teams,
    int IntervalMinutes,
    DateTime? NewestLastRunUtc,
    IngestionHealthState Health,
    TimeSpan StaleAfter,
    DateTime NowUtc)
{
    /// <summary>Whether the banner/warning should be shown at all — NeverPolled and Stale both warrant one, Healthy and NoTeams do not.</summary>
    public bool NeedsAttention => Health is IngestionHealthState.NeverPolled or IngestionHealthState.Stale;
}
