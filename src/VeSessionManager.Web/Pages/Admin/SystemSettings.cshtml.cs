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
    public DateTime? UpdatedUtc { get; private set; }

    public async Task OnGetAsync()
    {
        var settings = await systemSettingsService.GetAsync(CancellationToken.None);
        PiiRetentionWindowDays = settings.PiiRetentionWindowDays;
        FccDailyWatcherIntervalHours = settings.FccDailyWatcherIntervalHours;
        FccWeeklyCatchupIntervalHours = settings.FccWeeklyCatchupIntervalHours;
        FccWeeklyCatchupDayOfWeek = settings.FccWeeklyCatchupDayOfWeek;
        SessionIngestionIntervalMinutes = settings.SessionIngestionIntervalMinutes;
        UpdatedUtc = settings.UpdatedUtc;
    }

    public async Task<IActionResult> OnPostAsync(int? piiRetentionWindowDays, int fccDailyWatcherIntervalHours, int fccWeeklyCatchupIntervalHours, DayOfWeek fccWeeklyCatchupDayOfWeek, int sessionIngestionIntervalMinutes)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Forbid();
        }

        var result = await systemSettingsService.UpdateAsync(piiRetentionWindowDays, fccDailyWatcherIntervalHours, fccWeeklyCatchupIntervalHours, fccWeeklyCatchupDayOfWeek, sessionIngestionIntervalMinutes, user.Id, CancellationToken.None);
        if (result == SystemSettingsActionResult.Success)
        {
            TempData["StatusMessage"] = "System settings updated.";
        }
        else
        {
            TempData["ErrorMessage"] = "Could not save — intervals must be at least 1 minute (session ingestion) or 1 hour (ULS polling), and retention window (if set) at least 1 day.";
        }

        return RedirectToPage();
    }
}
