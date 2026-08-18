using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// "How long is this team's clock for this trigger point" — the one place anything outside the engine
/// is allowed to ask (#401, PR2).
///
/// <para><b>Why it exists.</b> Making <c>ParameterHours</c> editable turned two compile-time constants
/// into per-team data, and two things outside the rule engine were reading those constants: the
/// Applicant Status page's amber/red "days pending" colours, and the payment-expiry write in
/// <c>PaymentReminderService</c>. Both are *supposed* to agree with what the app actually does, so
/// both had to start reading the same rows the scanners read. A page confidently colouring a row red
/// on a day nothing happens is worse than no colour at all.</para>
///
/// <para><b>Earliest wins.</b> A team can have several rules on one trigger; the boundary anyone cares
/// about is the first one that fires, so that is what these return.</para>
/// </summary>
public class MessageThresholdService(AppDbContext dbContext)
{
    /// <summary>
    /// The hours a team's own bookkeeping should use — never null, because expiring a stale payment
    /// link has to keep happening for a team with no rule at all. Falls back to the trigger's default,
    /// which is the number that was hardcoded before rules existed.
    ///
    /// <para><b>Disabled rules count as absent</b>, and the fallback covers them. A team that switched
    /// the notice off is saying "stop telling people", not "stop expiring links" — the two were split
    /// for exactly that reason (see <c>PaymentReminderService.ProcessExpirationsAsync</c>).</para>
    /// </summary>
    public async Task<int> HoursOrDefaultAsync(int teamId, MessageTrigger trigger, CancellationToken cancellationToken)
    {
        var configured = await EarliestHoursAsync(teamId, trigger, cancellationToken);
        return configured ?? MessageTriggerDefinitions.For(trigger).DefaultParameterHours ?? 0;
    }

    /// <summary>
    /// The hours a team has actually configured, or null when it has no enabled rule for this trigger.
    ///
    /// <para><b>Null means "do not report a boundary", not "use the default"</b> — that is the whole
    /// difference from <see cref="HoursOrDefaultAsync"/>. A team with the FCC-fee reminder switched
    /// off has no day on which anything happens, so the Applicant Status page must not colour a row as
    /// though it does.</para>
    /// </summary>
    public Task<int?> ConfiguredHoursAsync(int teamId, MessageTrigger trigger, CancellationToken cancellationToken) =>
        EarliestHoursAsync(teamId, trigger, cancellationToken);

    /// <summary>
    /// The same answer for several teams at once, for a page that merges them. Teams with no enabled
    /// rule are simply absent from the dictionary rather than present with a null.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>> ConfiguredHoursByTeamAsync(
        IReadOnlyCollection<int> teamIds, MessageTrigger trigger, CancellationToken cancellationToken)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var rows = await dbContext.MessageRules
            .AsNoTracking()
            .Where(r => teamIds.Contains(r.TeamId) && r.Trigger == trigger && r.IsEnabled && r.ParameterHours != null)
            .Select(r => new { r.TeamId, Hours = r.ParameterHours!.Value })
            .ToListAsync(cancellationToken);

        // Grouped in memory: EF InMemory cannot translate Min() over a grouped projection reliably
        // (see the VolunteerExaminerReportService note in CLAUDE.md), and this is at most a handful of
        // rows per team.
        return rows
            .GroupBy(r => r.TeamId)
            .ToDictionary(g => g.Key, g => g.Min(r => r.Hours));
    }

    private async Task<int?> EarliestHoursAsync(int teamId, MessageTrigger trigger, CancellationToken cancellationToken)
    {
        var hours = await dbContext.MessageRules
            .AsNoTracking()
            .Where(r => r.TeamId == teamId && r.Trigger == trigger && r.IsEnabled && r.ParameterHours != null)
            .Select(r => r.ParameterHours!.Value)
            .ToListAsync(cancellationToken);

        return hours.Count == 0 ? null : hours.Min();
    }
}
