namespace VeSessionManager.Core.Entities;

public class FeeConfiguration
{
    public int Id { get; set; }

    /// <summary>Fee schedule is tied to the VEC in effect, since switching VECs is often exactly when the fee changes.</summary>
    public int VecId { get; set; }
    public Vec Vec { get; set; } = null!;

    public DateTime EffectiveDate { get; set; }

    public bool FeeCollectionEnabled { get; set; }

    /// <summary>Total amount charged to candidate; null if FeeCollectionEnabled = false.</summary>
    public decimal? ExamFeeAmount { get; set; }

    /// <summary>Portion kept for reimbursement; the remainder goes to the VEC. Null if FeeCollectionEnabled = false.</summary>
    public decimal? RetainedAmount { get; set; }

    public string? Notes { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedUtc { get; set; }

    public List<Session> Sessions { get; } = [];
}
