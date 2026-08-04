namespace VeSessionManager.Core.Payments;

/// <summary>Per-run counters, logged by the payment-generation job so JobRunHistory stays a one-line summary.</summary>
public class PaymentGenerationResult
{
    public int PaymentsCreated { get; set; }
    public int LinksGenerated { get; set; }
    public int LinksFailed { get; set; }

    /// <summary>Creations rejected by the unique index on (CandidateId, Reason) where a payment genuinely did exist afterwards — the other process (manual refresh vs. scheduled job) winning the race. Expected to be 0 almost always; a persistent non-zero value means the two are colliding often enough to look at.</summary>
    public int PaymentsSkippedAlreadyExisted { get; set; }

    /// <summary>Creations that failed for some *other* reason — no payment exists afterwards, so this is not the known race (a transient lock is the likely cause, since Web and Worker share one SQLite file). Distinguished from the above so a non-zero value here is never misdiagnosed as contention; the next run retries either way.</summary>
    public int PaymentsFailed { get; set; }

    public override string ToString() =>
        $"payments created {PaymentsCreated}, skipped (already existed) {PaymentsSkippedAlreadyExisted}, payments failed {PaymentsFailed}, links generated {LinksGenerated}, links failed {LinksFailed}";
}
