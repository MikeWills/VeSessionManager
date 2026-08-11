using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;

namespace VeSessionManager.Web.Pages;

// Public by design: A privacy policy behind a login is not a privacy policy.
[AllowAnonymous]
public class PrivacyModel(AppDbContext dbContext) : PageModel
{
    public int? PiiRetentionWindowDays { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.SystemSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        PiiRetentionWindowDays = settings?.PiiRetentionWindowDays;
    }
}
