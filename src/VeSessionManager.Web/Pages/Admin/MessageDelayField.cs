using VeSessionManager.Core.Messaging;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>
/// The days-to-hours boundary, shared by the three screens that let somebody set when a rule fires
/// (#401): new rule, edit rule, and the schedule panel on a template.
///
/// <para><b>Why it is here rather than left to the service.</b> <see cref="MessageDelay.ToHours"/>
/// answers null for two different things — "no delay" and "not a delay this unit can express" — and the
/// service, seeing only hours, would report the second as <c>ParameterRequired</c>: "this trigger needs
/// a number", said to somebody who typed one. Telling those apart needs the days value, which only
/// exists on this side of the boundary.</para>
/// </summary>
internal static class MessageDelayField
{
    /// <summary>Phrased in days because that is the box being complained about; the column's own ceiling is a year either way.</summary>
    internal const string RangeMessage =
        "The delay must be between half a day and 365 days, in steps of half a day.";

    internal const string RequiredMessage = "This trigger needs a number of days.";

    /// <summary>False when days were typed but cannot be honoured; <paramref name="hours"/> is then meaningless.</summary>
    internal static bool TryToHours(decimal? days, out int? hours)
    {
        hours = MessageDelay.ToHours(days);
        return days is null || hours is not null;
    }
}
