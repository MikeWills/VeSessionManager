namespace VeSessionManager.Core.Payments;

public class SquarePaymentLinkPurgeResult
{
    public int Purged { get; set; }
    public int Failed { get; set; }

    public override string ToString() => $"purged {Purged}, failed {Failed}";
}
