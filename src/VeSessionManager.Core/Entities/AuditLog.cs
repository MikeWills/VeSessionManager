using System.ComponentModel.DataAnnotations;

namespace VeSessionManager.Core.Entities;

public class AuditLog
{
    public int Id { get; set; }

    /// <summary>Null when the action was taken by a background job rather than a person (e.g. Phase 1's reschedule-flagged audit entry).</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Which team this entry belongs to, when that is knowable — the team attribution that makes a
    /// background-job entry visible to a TeamAdmin (#86 part 3).
    ///
    /// <para><b>Why it exists.</b> <see cref="UserId"/> is null for anything a job did, and
    /// <c>AdminAccessScope.ScopeAuditLog</c> filtered a TeamAdmin down to "actions taken by users on
    /// my team". Null-user rows matched nothing, so every automated action was invisible to them —
    /// candidates withdrawn from the feed, PII purged, Zoom/Discord cancellations — with nothing on
    /// the page to say so. There was no team on the row to filter on instead. Now there is.</para>
    ///
    /// <para><b>Null does not mean "no team", it means "not attributable to one".</b> Two cases, and
    /// keeping them both null is deliberate. A <see cref="VolunteerExaminer"/> is global here — one VE
    /// can sit on several teams' rosters (see docs/ve-management.md) — so a VE PII purge or a
    /// self-service email change genuinely belongs to no single team, and picking one would show it
    /// to a TeamAdmin with no claim on it. And every row written before this column existed is null,
    /// because only some of them could be backfilled. Either way a TeamAdmin does not see it and a
    /// SystemAdmin does, which is the same answer as before this column and so no worse.</para>
    ///
    /// <para>Not populated on the ~44 user-attributed call sites: those already scope correctly
    /// through the user's own team memberships, and setting it there would be a second source of
    /// truth for the same question.</para>
    /// </summary>
    public int? TeamId { get; set; }
    public Team? Team { get; set; }

    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public int EntityId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? Details { get; set; }

    /// <summary>
    /// Where the request came from, for <b>authentication events only</b> — sign-in, sign-in
    /// failure, lockout, password reset, and PII export. Null everywhere else, which is the vast
    /// majority of rows (#265).
    ///
    /// <para><b>Deliberately not populated for ordinary CRUD auditing.</b> The question this answers
    /// is "who signed in, from where, and how many times did they fail first" — without it a
    /// credential-stuffing run or a successful compromised-account login left nothing in the trail at
    /// all. "Which desk was this candidate edited from" is not a question anyone here needs, and
    /// answering it would turn an activity log into a movement record, in a table with no retention
    /// policy behind it yet (#313 is still open and needs-design).</para>
    ///
    /// <para>Correct behind the Apache reverse proxy because of <c>UseForwardedHeaders</c> in
    /// Program.cs — without that every row would read as the proxy's own loopback address, which is
    /// the same dependency the per-IP rate limiter has.</para>
    ///
    /// <para>Sized for an IPv6 address with an IPv4-mapped prefix. Null rather than empty when the
    /// address is genuinely unavailable, so "not recorded" and "recorded as nothing" stay
    /// distinguishable.</para>
    /// </summary>
    [MaxLength(45)]
    public string? SourceIpAddress { get; set; }
}
