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
    public async Task<(VecActionResult Result, Vec? Vec)> CreateAsync(string name, string? examToolsCode, bool supportsYouthProgram, string? notes, int userId, CancellationToken cancellationToken)
    {
        if (await dbContext.Vecs.AnyAsync(v => v.Name == name, cancellationToken))
        {
            return (VecActionResult.DuplicateName, null);
        }

        examToolsCode = NormalizeCode(examToolsCode, name);
        if (await MatchCodeIsTakenAsync(examToolsCode ?? name, excludingVecId: 0, cancellationToken))
        {
            return (VecActionResult.DuplicateExamToolsCode, null);
        }

        // Atomic where the provider allows it (issue #287) — same shape as the other create paths:
        // the audit needs the id the first save assigns.
        return await AtomicWrite.RunAsync(dbContext, async () =>
        {
            var vec = new Vec { Name = name, ExamToolsCode = examToolsCode, SupportsYouthProgram = supportsYouthProgram, Notes = notes };
            dbContext.Vecs.Add(vec);
            await dbContext.SaveChangesAsync(cancellationToken); // assigns vec.Id, needed for the audit entry below

            AddAudit(userId, "VecCreated", vec.Id, $"VEC '{name}' created.", timeProvider.GetUtcNow().UtcDateTime);
            await dbContext.SaveChangesAsync(cancellationToken);

            return (VecActionResult.Success, (Vec?)vec);
        }, cancellationToken);
    }

    public async Task<VecActionResult> UpdateAsync(int vecId, string name, string? examToolsCode, bool supportsYouthProgram, string? notes, int userId, CancellationToken cancellationToken)
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

        examToolsCode = NormalizeCode(examToolsCode, name);
        if (await MatchCodeIsTakenAsync(examToolsCode ?? name, excludingVecId: vecId, cancellationToken))
        {
            return VecActionResult.DuplicateExamToolsCode;
        }

        vec.Name = name;
        vec.ExamToolsCode = examToolsCode;
        vec.SupportsYouthProgram = supportsYouthProgram;
        vec.Notes = notes;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        AddAudit(userId, "VecUpdated", vec.Id, $"VEC '{name}' updated.", now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return VecActionResult.Success;
    }

    /// <summary>
    /// Blank means "same as the name" and is stored as null, so an admin who leaves the field empty
    /// gets the pre-ExamToolsCode behaviour. A code typed to exactly match the name is also stored
    /// as null rather than duplicating it — otherwise a later rename would silently strand the code
    /// on the old spelling.
    /// </summary>
    private static string? NormalizeCode(string? examToolsCode, string name)
    {
        var trimmed = examToolsCode?.Trim();
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    /// <summary>
    /// Ingestion matches on <c>ExamToolsCode ?? Name</c>, so a clash has to be checked against that
    /// same coalesce — a new VEC coded "lagroup" must be rejected if some other VEC is *named*
    /// "lagroup", not just if another one is coded that way.
    /// </summary>
    /// <remarks>
    /// <paramref name="excludingVecId"/> is 0 (never a real key) rather than a nullable int on
    /// purpose: <c>v.Id != null</c> translates to SQL <c>Id &lt;&gt; NULL</c>, which is NULL, so the
    /// create path would match zero rows and wave every duplicate through — and the InMemory
    /// provider used by the tests would not reproduce it.
    /// </remarks>
    private async Task<bool> MatchCodeIsTakenAsync(string matchCode, int excludingVecId, CancellationToken cancellationToken)
    {
        var lowered = matchCode.ToLowerInvariant();
        return await dbContext.Vecs.AnyAsync(
            v => v.Id != excludingVecId && (v.ExamToolsCode ?? v.Name).ToLower() == lowered,
            cancellationToken);
    }

    private void AddAudit(int userId, string action, int entityId, string details, DateTime now) =>
        dbContext.AddAuditLog(userId, action, nameof(Vec), entityId, details, now);
}

public enum VecActionResult
{
    Success,
    NotFound,
    DuplicateName,
    DuplicateExamToolsCode
}
