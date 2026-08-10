using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Worker;

/// <summary>
/// Development-only seed so Phase 1 ingestion has the Vec/FeeConfiguration/User rows it needs
/// before the Phase 9 admin UI exists to manage them. Never runs outside the Development
/// environment, and never touches a database that already has a fee configuration.
///
/// The guard is "does a FeeConfiguration exist", not "does a Vec exist": VecDefaultsSeeder now runs
/// first and always leaves Vec rows behind, so a table-wide Vec check would be true on a brand-new
/// dev database and this would seed nothing at all — the same shape as the DevAuthSeeder bug in
/// CLAUDE.md's Known Constraints. For the same reason the ARRL row is looked up rather than created.
/// </summary>
public static class DevDataSeeder
{
    public static async Task SeedAsync(AppDbContext dbContext, ILogger logger)
    {
        if (await dbContext.FeeConfigurations.AnyAsync())
        {
            return;
        }

        // Not an arbitrary/generic placeholder — SessionIngestionService matches this
        // case-insensitively against ExamTools' real per-session "vec" code, and the dev team's
        // real examtools.dev sessions all report "arrl". Normally VecDefaultsSeeder has just
        // created it; the fallback covers a database seeded by some other path.
        var vec = await dbContext.Vecs.FirstOrDefaultAsync(v => (v.ExamToolsCode ?? v.Name).ToLower() == "arrl")
                  ?? new Vec { Name = "ARRL", SupportsYouthProgram = true, Notes = "Dev seed row" };

        var systemUser = new User
        {
            Name = "System",
            Email = "system@localhost",
            Role = UserRole.SystemAdmin
        };

        dbContext.FeeConfigurations.Add(new FeeConfiguration
        {
            Vec = vec,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeCollectionEnabled = true,
            ExamFeeAmount = 15m,
            RetainedAmount = 7m,
            Notes = "Dev seed — 2026 fee schedule",
            CreatedByUser = systemUser,
            CreatedUtc = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync();
        logger.LogInformation("Seeded dev data: Vec {VecName}, System user, 2026 fee configuration", vec.Name);
    }
}
