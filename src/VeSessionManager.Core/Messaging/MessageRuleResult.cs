namespace VeSessionManager.Core.Messaging;

/// <summary>
/// Per-run counters for a rule pass, whose <see cref="ToString"/> becomes the <c>JobRunHistory</c>
/// summary line — the same job as <c>EmailNotificationResult</c>, with the two outcomes that column
/// could never express.
///
/// <para><see cref="Suppressed"/> and <see cref="Waiting"/> are the whole reason this is not just
/// "sent, failed". A run reporting "sent 0, failed 0" is indistinguishable from a quiet week, and
/// that is exactly what a muted team and a team with no SMTP credentials both used to look like on
/// the Job Run History page (#396).</para>
/// </summary>
public class MessageRuleResult
{
    public int Sent { get; set; }

    /// <summary>Email is switched off for the team. Settled — these will not be retried.</summary>
    public int Suppressed { get; set; }

    /// <summary>SMTP is not configured yet. <b>Not</b> settled — these go out on the first tick after credentials are entered.</summary>
    public int Waiting { get; set; }

    /// <summary>Nowhere to send it. Retried.</summary>
    public int NoRecipient { get; set; }

    /// <summary>The render or the send failed. Retried.</summary>
    public int Failed { get; set; }

    public void Add(MessageRuleResult other)
    {
        Sent += other.Sent;
        Suppressed += other.Suppressed;
        Waiting += other.Waiting;
        NoRecipient += other.NoRecipient;
        Failed += other.Failed;
    }

    /// <summary>Only the non-zero counts past "sent", so an ordinary run stays one short phrase and anything unusual is the thing that stands out.</summary>
    public override string ToString()
    {
        var parts = new List<string> { $"sent {Sent}" };
        if (Suppressed > 0) parts.Add($"suppressed {Suppressed}");
        if (Waiting > 0) parts.Add($"waiting on SMTP {Waiting}");
        if (NoRecipient > 0) parts.Add($"no recipient {NoRecipient}");
        if (Failed > 0) parts.Add($"failed {Failed}");
        return string.Join(", ", parts);
    }
}
