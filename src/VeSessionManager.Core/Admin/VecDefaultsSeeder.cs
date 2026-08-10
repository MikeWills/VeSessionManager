using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Seeds any of <see cref="KnownVecs"/> that the deployment doesn't already have. Like
/// EmailDefaultsSeeder (and unlike DevDataSeeder) this runs in every environment — a team onboarding
/// under a VEC nobody has typed in yet is exactly the case issue #83 exists for, and the failure
/// without it is silent: ingestion skips every session under the unmatched code.
///
/// **Existing rows are never modified.** A deployment that already created "ARRL" or coded GLAARG
/// as "lagroup" keeps its own name, notes and youth-program flag; the seeder only fills gaps.
/// </summary>
public static class VecDefaultsSeeder
{
    public static async Task<int> SeedAsync(AppDbContext dbContext, ILogger logger, CancellationToken cancellationToken = default)
    {
        // Reference table, at most a few dozen rows — materialize once rather than issuing 14
        // existence queries, and so MatchCode (a C# property, not translatable) can be used directly.
        var existing = await dbContext.Vecs.ToListAsync(cancellationToken);

        var takenMatchCodes = existing
            .Select(v => v.MatchCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenNames = existing
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seeded = 0;
        foreach (var known in KnownVecs.All)
        {
            if (takenMatchCodes.Contains(known.Code))
            {
                continue; // Already covered — by whatever name and code this deployment chose.
            }

            // The name is spoken for but resolves to some other code, so inserting would violate
            // IX_Vecs_Name. Near-certainly the same real-world VEC with a wrong or missing code,
            // which is the original silent-skip bug — say so instead of throwing.
            if (takenNames.Contains(known.Name))
            {
                logger.LogWarning(
                    "VEC '{VecName}' already exists but does not match ExamTools code '{ExamToolsCode}' — every session ExamTools reports under that code will be skipped. Set its ExamTools code in Admin -> VECs.",
                    known.Name, known.Code);
                continue;
            }

            // ExamToolsCode is null when it equals the name (VecManagementService.NormalizeCode does
            // the same on the admin path) so a later rename can't strand a stale code on the row.
            dbContext.Vecs.Add(new Vec
            {
                Name = known.Name,
                ExamToolsCode = string.Equals(known.Code, known.Name, StringComparison.OrdinalIgnoreCase) ? null : known.Code,
                SupportsYouthProgram = known.SupportsYouthProgram
            });

            takenMatchCodes.Add(known.Code);
            takenNames.Add(known.Name);
            seeded++;
        }

        if (seeded > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded {Count} VEC(s) from the known-VEC list", seeded);
        }

        WarnAboutUnknownMatchCodes(existing, logger);
        return seeded;
    }

    /// <summary>
    /// A pre-existing row whose match code isn't one of the fourteen can never match an ExamTools
    /// session — the code space is closed — so it is either a typo or a row that has silently been
    /// doing nothing. Worth one warning: it is also the case where the seeder above may have just
    /// added a correctly-coded row alongside it, leaving two rows for one VEC and any fee
    /// configuration attached to the dead one.
    /// </summary>
    private static void WarnAboutUnknownMatchCodes(List<Vec> existing, ILogger logger)
    {
        var knownCodes = KnownVecs.All.Select(v => v.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var vec in existing.Where(v => !knownCodes.Contains(v.MatchCode)))
        {
            logger.LogWarning(
                "VEC '{VecName}' resolves to ExamTools code '{MatchCode}', which is not one of the known VEC codes — ingestion will never match a session to it. Check for a typo in Admin -> VECs.",
                vec.Name, vec.MatchCode);
        }
    }
}
