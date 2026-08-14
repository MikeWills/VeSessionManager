using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web.Pages.Admin;

/// <summary>Phase 9c: deployment-wide settings (PII retention window, ULS polling intervals) — SystemAdmin only.</summary>
[Authorize(Roles = "SystemAdmin")]
public class SystemSettingsModel(UserManager<User> userManager, SystemSettingsService systemSettingsService) : PageModel
{
    public int? PiiRetentionWindowDays { get; private set; }
    public int UlsWatcherIntervalHours { get; private set; }
    public int UlsWatcherStartHourEt { get; private set; }
    public int SessionIngestionIntervalMinutes { get; private set; }
    public bool TestModeEnabled { get; private set; }
    public string? TestModeOverrideEmail { get; private set; }
    public DateTime? UpdatedUtc { get; private set; }

    public string? SystemSmtpHost { get; private set; }
    public int? SystemSmtpPort { get; private set; }
    public string? SystemSmtpUsername { get; private set; }
    public bool SystemSmtpUseStartTls { get; private set; }
    public string? SystemSmtpFromAddress { get; private set; }
    public string? SystemSmtpFromDisplayName { get; private set; }

    /// <summary>Whether a password is stored, never the password itself — a stored secret is never rendered back to the browser.</summary>
    public bool SystemSmtpPasswordIsSet { get; private set; }

    public bool IsSystemEmailConfigured { get; private set; }

    public async Task OnGetAsync()
    {
        var settings = await systemSettingsService.GetAsync(CancellationToken.None);
        SystemSmtpHost = settings.SystemSmtpHost;
        SystemSmtpPort = settings.SystemSmtpPort;
        SystemSmtpUsername = settings.SystemSmtpUsername;
        SystemSmtpUseStartTls = settings.SystemSmtpUseStartTls ?? true;
        SystemSmtpFromAddress = settings.SystemSmtpFromAddress;
        SystemSmtpFromDisplayName = settings.SystemSmtpFromDisplayName;
        SystemSmtpPasswordIsSet = !string.IsNullOrWhiteSpace(settings.SystemSmtpPassword);
        IsSystemEmailConfigured = settings.IsSystemEmailConfigured;
        PiiRetentionWindowDays = settings.PiiRetentionWindowDays;
        UlsWatcherIntervalHours = settings.UlsWatcherIntervalHours;
        UlsWatcherStartHourEt = settings.UlsWatcherStartHourEt;
        SessionIngestionIntervalMinutes = settings.SessionIngestionIntervalMinutes;
        TestModeEnabled = settings.TestModeEnabled;
        TestModeOverrideEmail = settings.TestModeOverrideEmail;
        UpdatedUtc = settings.UpdatedUtc;
    }

    public async Task<IActionResult> OnPostAsync(
        int? piiRetentionWindowDays, int ulsWatcherIntervalHours, int ulsWatcherStartHourEt,
        int sessionIngestionIntervalMinutes, bool testModeEnabled, string? testModeOverrideEmail)
    {
        // Role re-checked here, not just by the [Authorize(Roles = ...)] attribute (#257). The role
        // in the cookie is a claim baked in at sign-in; the row is the truth. SetRoleAsync now
        // rotates the security stamp, but revalidation is only every 30 minutes by default, and
        // during that window this handler rewrites deployment-wide SMTP credentials — the sender
        // used for password-reset mail — and the PII retention window.
        var user = await userManager.GetUserAsync(User);
        if (user is null || user.Role != UserRole.SystemAdmin)
        {
            return Forbid();
        }

        var result = await systemSettingsService.UpdateAsync(
            piiRetentionWindowDays, ulsWatcherIntervalHours, ulsWatcherStartHourEt,
            sessionIngestionIntervalMinutes, testModeEnabled, testModeOverrideEmail, user.Id, CancellationToken.None);
        TempData[result == SystemSettingsActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            SystemSettingsActionResult.Success => testModeEnabled ? "System settings updated. Test mode is ON — no real emails will be sent." : "System settings updated.",
            SystemSettingsActionResult.TestModeMissingOverrideEmail => "Could not save — an override email address is required to turn test mode on.",
            _ => "Could not save — intervals must be at least 1 minute (session ingestion) or 1 hour (ULS polling), the daily watcher start hour must be 0-23, and retention window (if set) at least 1 day."
        };

        return RedirectToPage();
    }

    /// <summary>
    /// The deployment-wide sender used for password-reset mail. Its own form and handler, separate
    /// from the settings above — a blank password field means "keep what's stored", so this form
    /// must not be entangled with the other one's save.
    /// </summary>
    public async Task<IActionResult> OnPostSystemEmailAsync(
        string? systemSmtpHost, int? systemSmtpPort, string? systemSmtpUsername, string? systemSmtpPassword,
        bool systemSmtpUseStartTls, string? systemSmtpFromAddress, string? systemSmtpFromDisplayName)
    {
        // Role re-checked here, not just by the [Authorize(Roles = ...)] attribute (#257). The role
        // in the cookie is a claim baked in at sign-in; the row is the truth. SetRoleAsync now
        // rotates the security stamp, but revalidation is only every 30 minutes by default, and
        // during that window this handler rewrites deployment-wide SMTP credentials — the sender
        // used for password-reset mail — and the PII retention window.
        var user = await userManager.GetUserAsync(User);
        if (user is null || user.Role != UserRole.SystemAdmin)
        {
            return Forbid();
        }

        var result = await systemSettingsService.UpdateSystemEmailAsync(
            systemSmtpHost, systemSmtpPort, systemSmtpUsername,
            // Blank => null => "leave the stored secret alone". Only a non-empty box changes it.
            string.IsNullOrWhiteSpace(systemSmtpPassword) ? null : systemSmtpPassword,
            systemSmtpUseStartTls, systemSmtpFromAddress, systemSmtpFromDisplayName,
            user.Id, CancellationToken.None);

        TempData[result == SystemSettingsActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            SystemSettingsActionResult.Success => "System email settings updated.",
            _ => "Could not save — the SMTP port must be between 1 and 65535."
        };

        return RedirectToPage();
    }
}
