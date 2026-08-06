using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Jobs;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// Admin → Job Schedule: every background job, its cadence, when it last ran and when it runs next.
///
/// <para>Deliberately read-only and <b>not team-scoped</b>. A job's schedule is a property of the
/// deployment, not of a team — several jobs are global, and the per-team ones still run on one shared
/// timer. Job History remains the place to see per-team outcomes; this page answers the question that
/// screen cannot, which is what happens <i>next</i>.</para>
///
/// <para>TeamAdmin can see it as well as SystemAdmin, matching Job History: knowing when the next
/// ingestion lands is exactly the question a team admin asks after changing something in ExamTools,
/// and nothing here is sensitive — no credentials, no candidate data, just timings.</para>
/// </summary>
[Authorize(Roles = "SystemAdmin,TeamAdmin")]
public class JobScheduleModel(JobScheduleService jobScheduleService, TimeProvider timeProvider) : PageModel
{
    public IReadOnlyList<JobScheduleStatus> Jobs { get; private set; } = [];

    /// <summary>Captured once so every relative "in 3 hours" on the page is measured from the same instant.</summary>
    public DateTime NowUtc { get; private set; }

    public async Task OnGetAsync()
    {
        NowUtc = timeProvider.GetUtcNow().UtcDateTime;
        Jobs = await jobScheduleService.GetStatusesAsync(HttpContext.RequestAborted);
    }

    /// <summary>
    /// "in 3 hours" / "12 minutes ago", to whatever precision is actually meaningful. Deliberately
    /// coarse: a next-run time derived from an interval is an estimate, and rendering it to the
    /// second would dress that up as more certain than it is.
    /// </summary>
    public static string DescribeRelative(DateTime utc, DateTime nowUtc)
    {
        var delta = utc - nowUtc;
        var future = delta > TimeSpan.Zero;
        var magnitude = delta.Duration();

        var text = magnitude switch
        {
            { TotalMinutes: < 1 } => "less than a minute",
            { TotalMinutes: < 60 } => Plural(magnitude.TotalMinutes, "minute"),
            { TotalHours: < 24 } => Plural(magnitude.TotalHours, "hour"),
            _ => Plural(magnitude.TotalDays, "day")
        };

        return future ? $"in {text}" : $"{text} ago";
    }

    private static string Plural(double value, string unit)
    {
        var rounded = (int)Math.Round(value);
        return rounded == 1 ? $"1 {unit}" : $"{rounded} {unit}s";
    }
}
