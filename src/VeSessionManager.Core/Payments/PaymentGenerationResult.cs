namespace VeSessionManager.Core.Payments;

/// <summary>Per-run counters, logged by the payment-generation job so JobRunHistory stays a one-line summary.</summary>
public class PaymentGenerationResult
{
    public int PaymentsCreated { get; set; }
    public int LinksGenerated { get; set; }
    public int LinksFailed { get; set; }

    public override string ToString() =>
        $"payments created {PaymentsCreated}, links generated {LinksGenerated}, links failed {LinksFailed}";
}
