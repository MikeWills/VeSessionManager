using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// The client address to record on an authentication audit row (#265).
///
/// <para>One definition, because there are several plausible-looking wrong ones. Reading
/// <c>X-Forwarded-For</c> by hand is the usual mistake: the header is attacker-supplied, so trusting
/// it directly lets anyone write whatever address they like into the security log — the very log
/// being written to establish where a sign-in came from. <c>UseForwardedHeaders</c> (Program.cs) is
/// what makes <c>RemoteIpAddress</c> correct behind Apache, and it only trusts the loopback proxy,
/// so this reads the resolved value and nothing else. The per-IP rate limiter depends on exactly the
/// same thing.</para>
/// </summary>
public static class SourceIp
{
    /// <summary>
    /// Null rather than a placeholder when there is genuinely no address — "not recorded" and
    /// "recorded as unknown" should not look the same in a security log. Truncated to the column
    /// width so an unexpectedly long value cannot fail the whole save; an audit row that loses a few
    /// characters is better than an action that rolls back because of its own logging.
    /// </summary>
    public static string? For(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() is { Length: > 0 } address
            ? address.Length <= 45 ? address : address[..45]
            : null;
}
