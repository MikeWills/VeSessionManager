namespace VeSessionManager.Web;

/// <summary>
/// Which rate-limit bucket a request path falls into.
///
/// <para>Extracted from the lambda in <c>Program.cs</c> so the rule is assertable. It is a
/// prefix-matching decision with a default of "no limit at all", which is exactly the shape where a
/// newly added endpoint silently gets nothing — and that is what happened to the Square webhook
/// (#264): it matched neither <c>/Account</c> nor <c>/VeSelfService</c>, so it fell through to
/// <c>GetNoLimiter</c> and sat outside every partition, doing a <c>Teams.FindAsync</c> plus an
/// HMAC-SHA256 over up to 64 KB per anonymous request before it could reject one.</para>
/// </summary>
public static class RateLimitPolicy
{
    public enum Bucket
    {
        /// <summary>No limit. Authenticated pages, static assets — cheap or already gated.</summary>
        Unlimited,

        /// <summary>
        /// Human-facing and abusable: sign-in, password reset, VE self-service. 20/minute per IP is
        /// far above real use (a login is one GET and one POST) and low enough to make brute-force
        /// and mail-flooding useless.
        /// </summary>
        Interactive,

        /// <summary>
        /// Machine callers. Its own bucket rather than joining Interactive because Square
        /// legitimately bursts — retries, a batch of payments settling at once — and 20/minute would
        /// drop deliveries the app then never learns about.
        /// </summary>
        Webhook
    }

    public const int InteractivePermitLimit = 20;
    public const int WebhookPermitLimit = 300;

    public static Bucket For(PathString path) =>
        path.StartsWithSegments("/webhooks") ? Bucket.Webhook
        : path.StartsWithSegments("/Account") || path.StartsWithSegments("/VeSelfService") ? Bucket.Interactive
        : Bucket.Unlimited;
}
