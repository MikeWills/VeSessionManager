using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Admin;

/// <summary>
/// Phase 9c: SystemAdmin-only deployment-wide settings (PII retention window, ULS polling
/// intervals, session ingestion interval). Backed by a single seeded singleton row (Id = 1, see
/// migration Phase9cSystemSettings) — GetAsync's get-or-create is a safety net, not the primary
/// seed path.
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
            UlsWatcherIntervalHours = 12,
            UlsWatcherStartHourEt = 8,
            SessionIngestionIntervalMinutes = 60
        };
        dbContext.SystemSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<SystemSettingsActionResult> UpdateAsync(
        int? piiRetentionWindowDays,
        int ulsWatcherIntervalHours,
        int ulsWatcherStartHourEt,
        int sessionIngestionIntervalMinutes,
        bool testModeEnabled,
        string? testModeOverrideEmail,
        int userId,
        CancellationToken cancellationToken)
    {
        if (ulsWatcherIntervalHours < 1 || piiRetentionWindowDays is < 1 || sessionIngestionIntervalMinutes < 1
            || ulsWatcherStartHourEt is < 0 or > 23)
        {
            return SystemSettingsActionResult.InvalidValue;
        }

        // Turning test mode on with no override address would silently drop every email instead of
        // redirecting it — require the address up front rather than discovering it at send time.
        if (testModeEnabled && string.IsNullOrWhiteSpace(testModeOverrideEmail))
        {
            return SystemSettingsActionResult.TestModeMissingOverrideEmail;
        }

        var settings = await GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        settings.PiiRetentionWindowDays = piiRetentionWindowDays;
        settings.UlsWatcherIntervalHours = ulsWatcherIntervalHours;
        settings.UlsWatcherStartHourEt = ulsWatcherStartHourEt;
        settings.SessionIngestionIntervalMinutes = sessionIngestionIntervalMinutes;
        settings.TestModeEnabled = testModeEnabled;
        settings.TestModeOverrideEmail = testModeOverrideEmail;
        settings.UpdatedByUserId = userId;
        settings.UpdatedUtc = now;

        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = "SystemSettingsUpdated",
            EntityType = nameof(SystemSettings),
            EntityId = SingletonId,
            TimestampUtc = now,
            // TestModeOverrideEmail is deliberately omitted from the audit trail — it's an admin's
            // own inbox address, not secret, but no other field here logs a raw email address either.
            Details = $"PiiRetentionWindowDays={piiRetentionWindowDays?.ToString() ?? "null"}, UlsWatcherIntervalHours={ulsWatcherIntervalHours}, UlsWatcherStartHourEt={ulsWatcherStartHourEt}, SessionIngestionIntervalMinutes={sessionIngestionIntervalMinutes}, TestModeEnabled={testModeEnabled}."
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return SystemSettingsActionResult.Success;
    }
}

public enum SystemSettingsActionResult
{
    Success,
    InvalidValue,
    TestModeMissingOverrideEmail
}
