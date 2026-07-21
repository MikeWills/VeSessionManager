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
    public int FccDailyWatcherIntervalHours { get; private set; }
    public int FccWeeklyCatchupIntervalHours { get; private set; }
    public DayOfWeek FccWeeklyCatchupDayOfWeek { get; private set; }
    public int SessionIngestionIntervalMinutes { get; private set; }
    public bool TestModeEnabled { get; private set; }
    public string? TestModeOverrideEmail { get; private set; }
    public DateTime? UpdatedUtc { get; private set; }

    public async Task OnGetAsync()
    {
        var settings = await systemSettingsService.GetAsync(CancellationToken.None);
        PiiRetentionWindowDays = settings.PiiRetentionWindowDays;
        FccDailyWatcherIntervalHours = settings.FccDailyWatcherIntervalHours;
        FccWeeklyCatchupIntervalHours = settings.FccWeeklyCatchupIntervalHours;
        FccWeeklyCatchupDayOfWeek = settings.FccWeeklyCatchupDayOfWeek;
        SessionIngestionIntervalMinutes = settings.SessionIngestionIntervalMinutes;
        TestModeEnabled = settings.TestModeEnabled;
        TestModeOverrideEmail = settings.TestModeOverrideEmail;
        UpdatedUtc = settings.UpdatedUtc;
    }

    public async Task<IActionResult> OnPostAsync(
        int? piiRetentionWindowDays, int fccDailyWatcherIntervalHours, int fccWeeklyCatchupIntervalHours, DayOfWeek fccWeeklyCatchupDayOfWeek,
        int sessionIngestionIntervalMinutes, bool testModeEnabled, string? testModeOverrideEmail)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        var result = await systemSettingsService.UpdateAsync(
            piiRetentionWindowDays, fccDailyWatcherIntervalHours, fccWeeklyCatchupIntervalHours, fccWeeklyCatchupDayOfWeek,
            sessionIngestionIntervalMinutes, testModeEnabled, testModeOverrideEmail, user.Id, CancellationToken.None);
        TempData[result == SystemSettingsActionResult.Success ? "StatusMessage" : "ErrorMessage"] = result switch
        {
            SystemSettingsActionResult.Success => testModeEnabled ? "System settings updated. Test mode is ON — no real emails will be sent." : "System settings updated.",
            SystemSettingsActionResult.TestModeMissingOverrideEmail => "Could not save — an override email address is required to turn test mode on.",
            _ => "Could not save — intervals must be at least 1 minute (session ingestion) or 1 hour (ULS polling), and retention window (if set) at least 1 day."
        };

        return RedirectToPage();
    }
}
