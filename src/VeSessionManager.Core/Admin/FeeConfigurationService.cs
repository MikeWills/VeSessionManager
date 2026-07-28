using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: FeeConfiguration CRUD (create + edit-before-use only, no delete). Session.FeeConfiguration
/// is a live navigation, not a snapshot copy, so editing a row a Session already references would
/// retroactively change that session's fee data — UpdateAsync blocks that (InUse). The correct flow
/// for "the fee changed" is always CreateAsync a new dated row; UpdateAsync exists only to fix a
/// mistake on a row before any session has used it.
/// </summary>
public class FeeConfigurationService(AppDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<(FeeConfigActionResult Result, FeeConfiguration? FeeConfiguration)> CreateAsync(
        int vecId, DateTime effectiveDate, bool feeCollectionEnabled, decimal? examFeeAmount, decimal? retainedAmount, decimal? youthExamFeeAmount, string? notes, int userId, CancellationToken cancellationToken)
    {
        var vecExists = await dbContext.Vecs.AnyAsync(v => v.Id == vecId, cancellationToken);
        if (!vecExists)
        {
            return (FeeConfigActionResult.VecNotFound, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var feeConfiguration = new FeeConfiguration
        {
            VecId = vecId,
            EffectiveDate = effectiveDate,
            FeeCollectionEnabled = feeCollectionEnabled,
            ExamFeeAmount = feeCollectionEnabled ? examFeeAmount : null,
            RetainedAmount = feeCollectionEnabled ? retainedAmount : null,
            YouthExamFeeAmount = feeCollectionEnabled ? youthExamFeeAmount : null,
            Notes = notes,
            CreatedByUserId = userId,
            CreatedUtc = now
        };
        dbContext.FeeConfigurations.Add(feeConfiguration);
        await dbContext.SaveChangesAsync(cancellationToken);

        AddAudit(userId, "FeeConfigurationCreated", feeConfiguration.Id, $"Fee configuration created for VEC {vecId}, effective {effectiveDate:d}.", now);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (FeeConfigActionResult.Success, feeConfiguration);
    }

    public async Task<FeeConfigActionResult> UpdateAsync(
        int feeConfigurationId, DateTime effectiveDate, bool feeCollectionEnabled, decimal? examFeeAmount, decimal? retainedAmount, decimal? youthExamFeeAmount, string? notes, int userId, CancellationToken cancellationToken)
    {
        var feeConfiguration = await dbContext.FeeConfigurations.FirstOrDefaultAsync(f => f.Id == feeConfigurationId, cancellationToken);
        if (feeConfiguration is null)
        {
            return FeeConfigActionResult.NotFound;
        }

        var inUse = await dbContext.Sessions.AnyAsync(s => s.FeeConfigurationId == feeConfigurationId, cancellationToken);
        if (inUse)
        {
            return FeeConfigActionResult.InUse;
        }

        feeConfiguration.EffectiveDate = effectiveDate;
        feeConfiguration.FeeCollectionEnabled = feeCollectionEnabled;
        feeConfiguration.ExamFeeAmount = feeCollectionEnabled ? examFeeAmount : null;
        feeConfiguration.RetainedAmount = feeCollectionEnabled ? retainedAmount : null;
        feeConfiguration.YouthExamFeeAmount = feeCollectionEnabled ? youthExamFeeAmount : null;
        feeConfiguration.Notes = notes;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(userId, "FeeConfigurationUpdated", feeConfiguration.Id, $"Fee configuration {feeConfigurationId} updated.", now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return FeeConfigActionResult.Success;
    }

    private void AddAudit(int userId, string action, int entityId, string details, DateTime now) =>
        dbContext.AddAuditLog(userId, action, nameof(FeeConfiguration), entityId, details, now);
}

public enum FeeConfigActionResult
{
    Success,
    NotFound,
    VecNotFound,
    InUse
}
