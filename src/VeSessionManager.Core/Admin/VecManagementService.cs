using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: SystemAdmin-only management of Vec rows — shared/global reference data, not per-team
/// (see the multi-team foundation's VEC=>Team=>VE hierarchy). No delete — matches the app-wide rule
/// that rows with dependents are never hard-deleted (every Vec FK is DeleteBehavior.Restrict).
/// </summary>
public class VecManagementService(AppDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<(VecActionResult Result, Vec? Vec)> CreateAsync(string name, bool supportsYouthProgram, string? notes, int userId, CancellationToken cancellationToken)
    {
        if (await dbContext.Vecs.AnyAsync(v => v.Name == name, cancellationToken))
        {
            return (VecActionResult.DuplicateName, null);
        }

        var vec = new Vec { Name = name, SupportsYouthProgram = supportsYouthProgram, Notes = notes };
        dbContext.Vecs.Add(vec);
        await dbContext.SaveChangesAsync(cancellationToken); // assigns vec.Id, needed for the audit entry below

        AddAudit(userId, "VecCreated", vec.Id, $"VEC '{name}' created.", timeProvider.GetUtcNow().UtcDateTime);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (VecActionResult.Success, vec);
    }

    public async Task<VecActionResult> UpdateAsync(int vecId, string name, bool supportsYouthProgram, string? notes, int userId, CancellationToken cancellationToken)
    {
        var vec = await dbContext.Vecs.FirstOrDefaultAsync(v => v.Id == vecId, cancellationToken);
        if (vec is null)
        {
            return VecActionResult.NotFound;
        }

        if (vec.Name != name && await dbContext.Vecs.AnyAsync(v => v.Id != vecId && v.Name == name, cancellationToken))
        {
            return VecActionResult.DuplicateName;
        }

        vec.Name = name;
        vec.SupportsYouthProgram = supportsYouthProgram;
        vec.Notes = notes;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(userId, "VecUpdated", vec.Id, $"VEC '{name}' updated.", now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return VecActionResult.Success;
    }

    private void AddAudit(int userId, string action, int entityId, string details, DateTime now) =>
        dbContext.AddAuditLog(userId, action, nameof(Vec), entityId, details, now);
}

public enum VecActionResult
{
    Success,
    NotFound,
    DuplicateName
}
