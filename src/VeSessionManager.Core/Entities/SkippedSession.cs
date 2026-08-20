namespace VeSessionManager.Core.Entities;

/// <summary>
/// A session ExamTools is reporting that this app refuses to create, because it has nothing to
/// configure it with (#440, split out of #402).
///
/// <para><b>Why a row and not just the counter.</b> Both skip sites already logged a <c>[WRN]</c> and
/// bumped <c>IngestionResult.SessionsSkippedNoConfig</c> — a number inside a run summary whose status
/// is <c>Success</c>. On beta that ran for <b>five days</b> and surfaced only because a Session
/// Manager noticed a colleague's session had never appeared. A counter cannot become an alert; it has
/// nowhere to point and nothing to name. This row is what lets the bell say <i>"Aug 18 W9NB — no VEC
/// configured for 'arrl'"</i>, which states the fix rather than the symptom.</para>
///
/// <para><b>Why it is easy to miss without one.</b> The config check runs only on create, so every
/// session already in the table keeps updating normally. The app looks healthy and only <i>new</i>
/// sessions vanish — the hardest kind of missing to notice.</para>
///
/// <para><b>Nobody dismisses these.</b> The row is a statement about the current configuration, not a
/// task: it clears the moment the session ingests, and it is swept away when the feed stops reporting
/// the session at all. A dismiss button would let somebody silence a live misconfiguration.</para>
/// </summary>
public class SkippedSession
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    /// <summary>The remote session's id. Unique per team — one row per session, re-stamped rather than duplicated on every poll.</summary>
    public string ExamToolsSessionId { get; set; } = string.Empty;

    /// <summary>Whatever ExamTools called the VEC ("arrl", "lagroup"). The value that failed to match, quoted back verbatim, because it is the string somebody has to type into Admin → VECs.</summary>
    public string VecCode { get; set; } = string.Empty;

    /// <summary>The session's title as the feed reports it, so the alert can name a session a human recognizes rather than an opaque id.</summary>
    public string? Title { get; set; }

    /// <summary>When the session is scheduled — the other half of recognizing it.</summary>
    public DateTime? ScheduledStartUtc { get; set; }

    public SkippedSessionReason Reason { get; set; }

    /// <summary>When this was first refused. The alert's <c>OccurredUtc</c>: "how long has this been broken", which is the question that matters for a silent fault.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Refreshed every run the session is still being refused. A row whose last-seen predates the current run is stale and gets swept — the feed has stopped reporting that session.</summary>
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>Which of the two configuration faults refused the session — they have different fixes, and therefore different destinations on the alert.</summary>
public enum SkippedSessionReason
{
    /// <summary>No <c>Vec</c> matches the ExamTools code. Fix: add or correct the code in Admin → VECs.</summary>
    NoMatchingVec = 0,

    /// <summary>The VEC matched but has no <c>FeeConfiguration</c> in effect. Fix: add one in Admin → Fee Configurations. Never seen live, and identically silent if it happens.</summary>
    NoFeeConfiguration = 1
}
