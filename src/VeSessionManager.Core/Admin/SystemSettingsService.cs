using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: SystemAdmin-only deployment-wide settings (PII retention window, ULS polling
/// intervals). Backed by a single seeded singleton row (Id = 1, see migration
/// Phase9cSystemSettings) — GetAsync's get-or-create is a safety net, not the primary seed path.
/// </summary>
public class SystemSettingsService(AppDbContext dbContext, TimeProvider timeProvider)
{
    public const int SingletonId = 1;

    public async Task<SystemSettings> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.SystemSettings.FirstOrDefaultAsync(s => s.Id == SingletonId, cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new SystemSettings
        {
            Id = SingletonId,
            FccDailyWatcherIntervalHours = 24,
            FccWeeklyCatchupIntervalHours = 24,
            FccWeeklyCatchupDayOfWeek = DayOfWeek.Monday
        };
        dbContext.SystemSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<SystemSettingsActionResult> UpdateAsync(
        int? piiRetentionWindowDays,
        int fccDailyWatcherIntervalHours,
        int fccWeeklyCatchupIntervalHours,
        DayOfWeek fccWeeklyCatchupDayOfWeek,
        int userId,
        CancellationToken cancellationToken)
    {
        if (fccDailyWatcherIntervalHours < 1 || fccWeeklyCatchupIntervalHours < 1 || piiRetentionWindowDays is < 1)
        {
            return SystemSettingsActionResult.InvalidValue;
        }

        var settings = await GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        settings.PiiRetentionWindowDays = piiRetentionWindowDays;
        settings.FccDailyWatcherIntervalHours = fccDailyWatcherIntervalHours;
        settings.FccWeeklyCatchupIntervalHours = fccWeeklyCatchupIntervalHours;
        settings.FccWeeklyCatchupDayOfWeek = fccWeeklyCatchupDayOfWeek;
        settings.UpdatedByUserId = userId;
        settings.UpdatedUtc = now;

        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "SystemSettingsUpdated",
            EntityType = nameof(SystemSettings),
            EntityId = SingletonId,
            TimestampUtc = now,
            Details = $"PiiRetentionWindowDays={piiRetentionWindowDays?.ToString() ?? "null"}, FccDailyWatcherIntervalHours={fccDailyWatcherIntervalHours}, FccWeeklyCatchupIntervalHours={fccWeeklyCatchupIntervalHours}, FccWeeklyCatchupDayOfWeek={fccWeeklyCatchupDayOfWeek}."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return SystemSettingsActionResult.Success;
    }
}

public enum SystemSettingsActionResult
{
    Success,
    InvalidValue
}
