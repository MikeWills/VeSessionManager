using System.ComponentModel.DataAnnotations;

namespace VeSessionManager.Core.Entities;

public class AuditLog
{
    public int Id { get; set; }

    /// <summary>Null when the action was taken by a background job rather than a person (e.g. Phase 1's reschedule-flagged audit entry).</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

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
