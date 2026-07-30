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

    /// <summary>
    /// What actually goes to the VEC for a given charged amount — clamped at zero, not a plain
    /// subtraction: the youth/scholarship rate (YouthExamFeeAmount) can be, and for ARRL currently
    /// is, less than RetainedAmount (e.g. a $5 youth fee against a $7 retained cap), meaning the team
    /// keeps the entire youth fee and nothing is owed to the VEC — a naive `charged - RetainedAmount`
    /// would go negative instead. Null (not zero) when RetainedAmount itself isn't set, since that
    /// means "unknown," not "nothing owed." This is the per-candidate default; a session can instead
    /// retain a flat total for the whole session (real per-session expenses vary and aren't a
    /// per-candidate cost) — see Session.RetainedAmountOverride/Session.GetFeeSummary.
    /// </summary>
    public decimal? RemitToVecAmount(decimal chargedAmount) =>
        RetainedAmount is null ? null : Math.Max(0m, chargedAmount - RetainedAmount.Value);

    /// <summary>Sibling to ExamFeeAmount for the Vec's youth/scholarship rate (e.g. ARRL's $5) —
    /// null means the youth confirmation flow isn't available for this fee schedule yet, even if
    /// Vec.SupportsYouthProgram is true (surfaced as a friendly "not set up" message rather than a
    /// hardcoded fallback, since the amount is VEC-specific, not universal). See
    /// docs/youth-payment-confirmation.md.</summary>
    public decimal? YouthExamFeeAmount { get; set; }

    public string? Notes { get; set; }

    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedUtc { get; set; }

    public List<Session> Sessions { get; } = [];
}
