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

    /// <summary>
    /// The seeded default for SessionIngestionIntervalMinutes, shared so a read-only caller that
    /// must not trigger GetAsync's get-or-create write (a page render — see IngestionStatusService)
    /// can fall back to the same number this service would have created, rather than its own guess.
    /// </summary>
    public const int DefaultSessionIngestionIntervalMinutes = 60;

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
            SessionIngestionIntervalMinutes = DefaultSessionIngestionIntervalMinutes
        };
        dbContext.SystemSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<SystemSettingsActionResult> UpdateAsync(
        int? piiRetentionWindowDays,
        int? veContactRetentionYears,
        int? auditLogRetentionDays,
        int? jobRunHistoryRetentionDays,
        int ulsWatcherIntervalHours,
        int ulsWatcherStartHourEt,
        int sessionIngestionIntervalMinutes,
        bool testModeEnabled,
        string? testModeOverrideEmail,
        int userId,
        CancellationToken cancellationToken)
    {
        if (ulsWatcherIntervalHours < 1 || piiRetentionWindowDays is < 1 || sessionIngestionIntervalMinutes < 1
            || ulsWatcherStartHourEt is < 0 or > 23 || veContactRetentionYears is < 1
            || auditLogRetentionDays is < 1 || jobRunHistoryRetentionDays is < 1)
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
        settings.VeContactRetentionYears = veContactRetentionYears;
        settings.AuditLogRetentionDays = auditLogRetentionDays;
        settings.JobRunHistoryRetentionDays = jobRunHistoryRetentionDays;
        settings.UlsWatcherIntervalHours = ulsWatcherIntervalHours;
        settings.UlsWatcherStartHourEt = ulsWatcherStartHourEt;
        settings.SessionIngestionIntervalMinutes = sessionIngestionIntervalMinutes;
        settings.TestModeEnabled = testModeEnabled;
        settings.TestModeOverrideEmail = testModeOverrideEmail;
        settings.UpdatedByUserId = userId;
        settings.UpdatedUtc = now;

        // TestModeOverrideEmail is deliberately omitted from the audit trail — it's an admin's
        // own inbox address, not secret, but no other field here logs a raw email address either.
        dbContext.AddAuditLog(userId, "SystemSettingsUpdated", nameof(SystemSettings), SingletonId,
            $"PiiRetentionWindowDays={piiRetentionWindowDays?.ToString() ?? "null"}, VeContactRetentionYears={veContactRetentionYears?.ToString() ?? "null"}, AuditLogRetentionDays={auditLogRetentionDays?.ToString() ?? "null"}, JobRunHistoryRetentionDays={jobRunHistoryRetentionDays?.ToString() ?? "null"}, UlsWatcherIntervalHours={ulsWatcherIntervalHours}, UlsWatcherStartHourEt={ulsWatcherStartHourEt}, SessionIngestionIntervalMinutes={sessionIngestionIntervalMinutes}, TestModeEnabled={testModeEnabled}.",
            now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return SystemSettingsActionResult.Success;
    }

    /// <summary>
    /// The deployment-wide ("system") SMTP sender used for app-user mail — password reset today.
    /// Separate from UpdateAsync rather than seven more parameters on an already-eight-parameter
    /// method, and it keeps its own audit line.
    ///
    /// <paramref name="password"/> follows TeamSettingsService's convention: **null means "leave the
    /// stored secret alone"**, so an admin editing the host or username doesn't have to retype the
    /// password (and the page never has to render it back into the DOM to preserve it).
    /// </summary>
    public async Task<SystemSettingsActionResult> UpdateSystemEmailAsync(
        string? host,
        int? port,
        string? username,
        string? password,
        bool? useStartTls,
        string? fromAddress,
        string? fromDisplayName,
        int userId,
        CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            return SystemSettingsActionResult.InvalidValue;
        }

        var settings = await GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        settings.SystemSmtpHost = Blank(host);
        settings.SystemSmtpPort = port;
        settings.SystemSmtpUsername = Blank(username);
        settings.SystemSmtpUseStartTls = useStartTls;
        settings.SystemSmtpFromAddress = Blank(fromAddress);
        settings.SystemSmtpFromDisplayName = Blank(fromDisplayName);
        if (password is not null)
        {
            settings.SystemSmtpPassword = password;
        }

        settings.UpdatedByUserId = userId;
        settings.UpdatedUtc = now;

        dbContext.AddAuditLog(userId, "SystemEmailSettingsUpdated", nameof(SystemSettings), SingletonId,
            // Host/port/TLS only. The username is an account identifier and the password is a
            // secret — neither belongs in an audit trail, same rule the rest of this file follows.
            $"System SMTP updated: host={Blank(host) ?? "(none)"}, port={port?.ToString() ?? "(default)"}, startTls={useStartTls?.ToString() ?? "(default)"}, passwordChanged={password is not null}.",
            now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return SystemSettingsActionResult.Success;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum SystemSettingsActionResult
{
    Success,
    InvalidValue,
    TestModeMissingOverrideEmail
}
