using System.Globalization;

namespace VeSessionManager.Core.Messaging;

/// <summary>
/// Days on the screen, hours in the database (#401) — the one place that conversion lives.
///
/// <para><b>Why the two differ.</b> Every admin form asks for days, because that is how a team thinks
/// about a reminder: "a day before", "five days after the FCC got it". <c>MessageRule.ParameterHours</c>
/// stays hours because that is the unit the scanners compare instants in, and because it is the unit
/// that makes the #220 class of bug impossible to express — see below.</para>
///
/// <para><b>Days here means a duration, never a calendar date.</b> Two days before a session is
/// forty-eight hours before it starts, not "the calendar date two days earlier". That distinction is
/// the whole reason the stored value is hours: sessions run in the evening Eastern, which is already
/// tomorrow in UTC, so a calendar-date reminder went out on the day of the session. Multiplying by 24
/// keeps the model a duration while letting the form read naturally.</para>
///
/// <para><b>Halves are allowed, and that is what keeps sub-day timing reachable.</b> A whole-numbers-only
/// day field would have quietly removed "12 hours before the session" — a real thing a team may want and
/// something the hours field could always express. <see cref="Step"/> is half a day, so 0.5 is twelve
/// hours and 1.5 is thirty-six, and <see cref="Minimum"/> is the smallest delay anybody can set. Finer
/// than that is refused rather than rounded: an odd number of hours cannot be written in this unit
/// without lying about it, and a form that silently turns 0.3 into 7 hours is worse than one that says no.</para>
/// </summary>
/// <summary>
/// The unit a delay is typed in. Added for #116, which needs a reminder an hour before a session —
/// something a day-denominated field put a <b>12-hour floor</b> under.
///
/// <para>The unit moved rather than the precision. <see cref="MessageDelay"/> refused finer than half
/// a day on the grounds that an odd number of hours cannot be written in days without lying about it,
/// and that a form silently turning 0.3 into 7 hours is worse than one that says no. Both still hold —
/// so hours became sayable instead of days becoming vaguer.</para>
/// </summary>
public enum MessageDelayUnit
{
    Days = 0,
    Hours = 1
}

public static class MessageDelay
{
    public const int HoursPerDay = 24;

    /// <summary>Half a day — twelve hours, the finest a day-denominated field can express honestly.</summary>
    public const decimal Step = 0.5m;

    /// <summary>The shortest delay settable, equal to <see cref="Step"/>: twelve hours.</summary>
    public const decimal Minimum = Step;

    /// <summary>A year, matching <c>MessageRuleAdminService.MaxParameterHours</c> — the same guard against a typo that reads as a working rule.</summary>
    public const decimal Maximum = 365m;

    /// <summary>
    /// Null in, null out: a state trigger has no delay, and that is different from a delay of zero.
    /// Returns null too for a value that is not a whole number of half-days, which the caller reports
    /// as out of range rather than rounding away.
    /// </summary>
    public static int? ToHours(decimal? days)
    {
        if (days is not { } d) return null;
        if (d < Minimum || d > Maximum) return null;
        var hours = d * HoursPerDay;
        return hours == decimal.Truncate(hours) && hours % (HoursPerDay * Step) == 0
            ? (int)hours
            : null;
    }

    /// <summary>The smallest delay in hours. One hour — what #116 asks for, and the finest the stored column can hold.</summary>
    public const decimal MinimumHours = 1m;

    /// <summary>A year in hours, the same ceiling <c>MessageRuleAdminService.MaxParameterHours</c> enforces.</summary>
    public const decimal MaximumHours = 365m * HoursPerDay;

    /// <summary>A typed value in its unit, as whole hours — or null when the unit cannot express it honestly.</summary>
    /// <remarks>
    /// A fractional hour is refused for exactly the reason a third of a day always was: the stored
    /// column is whole hours, and quietly turning 1.5 into 1 is a rule that fires at a time nobody chose.
    /// </remarks>
    public static int? ToHours(decimal? value, MessageDelayUnit unit)
    {
        if (unit == MessageDelayUnit.Days)
        {
            return ToHours(value);
        }

        if (value is not { } hours) return null;
        if (hours < MinimumHours || hours > MaximumHours) return null;
        return hours == decimal.Truncate(hours) ? (int)hours : null;
    }

    /// <summary>
    /// A stored value in the unit that reads naturally: whole (or half) days as days, anything else as
    /// hours. 36 hours is a day and a half; 1 hour has no honest day form, which is the whole point.
    /// </summary>
    public static (decimal Value, MessageDelayUnit Unit)? ForDisplay(int? hours)
    {
        if (hours is not { } h) return null;

        // Expressible in days only when it is a whole number of half-days — the same rule the days
        // field enforces, so what comes back is always something that field would accept.
        var halfDay = (int)(HoursPerDay * Step);
        return h % halfDay == 0 && h >= HoursPerDay * Minimum
            ? (h / (decimal)HoursPerDay, MessageDelayUnit.Days)
            : (h, MessageDelayUnit.Hours);
    }

    /// <summary>
    /// Hours back into days for the form. A stored value that is not a whole number of half-days can
    /// only predate this field (or come from a hand-edited row); it is shown to its nearest half rather
    /// than blank, and saving normalises it.
    /// </summary>
    public static decimal? ToDays(int? hours) =>
        hours is { } h ? Math.Round(h / (decimal)HoursPerDay / Step, MidpointRounding.AwayFromZero) * Step : null;

    /// <summary>"1", "0.5", "7.5" — no trailing zeroes, invariant so the form posts what the browser expects.</summary>
    public static string Format(decimal days) =>
        days.ToString("0.##", CultureInfo.InvariantCulture);
}
