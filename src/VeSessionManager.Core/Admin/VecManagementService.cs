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

        examToolsCode = NormalizeCode(examToolsCode);
        // Vec.Name is a required column; see TeamSettingsService.CreateAsync for the reasoning (#275).
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            return (VecActionResult.NameRequired, null);
        }

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

        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            return VecActionResult.NameRequired;
        }

        if (vec.Name != name && await dbContext.Vecs.AnyAsync(v => v.Id != vecId && v.Name == name, cancellationToken))
        {
            return VecActionResult.DuplicateName;
        }

        examToolsCode = NormalizeCode(examToolsCode);

        // A rename must never move what ingestion matches on (#402). See FreezeCodeOnRename.
        examToolsCode = FreezeCodeOnRename(vec, name, examToolsCode);

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
    /// gets the pre-ExamToolsCode behavior.
    ///
    /// <para><b>A code typed to match the name is now stored, not discarded (#402).</b> It used to be
    /// nulled, on the reasoning that "otherwise a later rename would silently strand the code on the
    /// old spelling" — which had it exactly backwards. Stranding it on the old spelling is the correct
    /// outcome: ExamTools' <c>vec</c> value is upstream data, and nothing done to a local display label
    /// can change it. Discarding the code is what let a rename re-point the match.</para>
    /// </summary>
    private static string? NormalizeCode(string? examToolsCode)
    {
        var trimmed = examToolsCode?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Keeps a rename cosmetic (#402). A VEC with no code is matched on its <b>name</b>, so renaming
    /// one silently changes which ExamTools sessions it accepts — on the beta box that skipped five
    /// HRCC sessions for five days while every ingestion run still reported <c>Success</c>.
    ///
    /// <para>Applies only where the code was <i>already</i> implicit and the submitted one is still
    /// blank: nothing about the code was touched, but the name moved out from under it, so the old
    /// name is written down as what it always effectively was. A typed code wins (the admin renaming
    /// because the old name was wrong), and clearing a code that was really set is honoured as the
    /// deliberate "match on the name" it is.</para>
    /// </summary>
    private static string? FreezeCodeOnRename(Vec vec, string newName, string? submittedCode) =>
        submittedCode is null && vec.ExamToolsCode is null && !string.Equals(vec.Name, newName, StringComparison.Ordinal)
            ? vec.Name
            : submittedCode;

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

    /// <summary>A required value arrived blank — see RequiredInputGuardTests for why this is checked here rather than on the page (issue #275).</summary>
    NameRequired,
    DuplicateExamToolsCode
}
