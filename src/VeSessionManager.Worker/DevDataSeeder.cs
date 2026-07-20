using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Worker;

/// <summary>
/// Development-only seed so Phase 1 ingestion has the Vec/FeeConfiguration/User rows it needs
/// before the Phase 9 admin UI exists to manage them. Never runs outside the Development
/// environment, and never touches a database that already has a Vec.
/// </summary>
public static class DevDataSeeder
{
    public static async Task SeedAsync(AppDbContext dbContext, ILogger logger)
    {
        if (await dbContext.Vecs.AnyAsync())
        {
            return;
        }

        var vec = new Vec
        {
            // Not an arbitrary/generic placeholder — SessionIngestionService matches this
            // case-insensitively against ExamTools' real per-session "vec" code, and the dev
            // team's real examtools.dev sessions all report "arrl". Change this only if the dev
            // team ever starts running sessions under a different VEC.
            Name = "ARRL",
            SupportsYouthProgram = true,
            Notes = "Dev seed row"
        };

        var systemUser = new User
        {
            Name = "System",
            Email = "system@localhost",
            Role = UserRole.Admin
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
