using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Creates the setup SystemAdmin on a deployment where **nobody can sign in at all**, so a fresh
/// server is usable without hand-editing the database or reading a password out of a log.
///
/// **The credentials are fixed and documented in the README** — a deliberate product decision
/// (2026-08-01). A generated password is one more thing to hunt for at exactly the moment someone is
/// trying to get started.
///
/// The exposure this accepts, recorded so it is not rediscovered later as a surprise: between a
/// deployment first starting and a real SystemAdmin being created, **the published credentials
/// work**. On a fresh box that means taking over an empty system — no teams, no integration
/// credentials, no ingested candidates, so there is nothing in it to read. Finishing setup closes
/// the window, and the banner on every page pushes you to do exactly that.
///
/// Two things stop the window becoming permanent:
///  - It only ever exists **while no account has a password**. A deployment set up with
///    <see cref="BootstrapAdminCommand"/> (`--create-admin`) never creates this account at all.
///  - It is **retired automatically** — <c>UserManagementService.CreateAsync</c> deactivates it the
///    moment a real SystemAdmin is created, so it is not a cleanup step anyone has to remember.
///
/// `--create-admin` is still the stricter option (password from the environment, no shared-credential
/// account ever created) and is preferable for anything internet-facing. See docs/deployment.md.
/// </summary>
public static class BootstrapAdminSeeder
{
    /// <summary>Must stay in step with UserManagementService.BootstrapAdminEmail, which is what retires this account automatically.</summary>
    public const string Email = UserManagementService.BootstrapAdminEmail;

    /// <summary>
    /// Documented in the README. Satisfies Program.cs's Identity policy (12+ characters, digit,
    /// upper, lower). Changing it breaks the published setup instructions.
    /// </summary>
    public const string Password = "Setup-Password1";

    public static async Task SeedAsync(IServiceProvider services, ILogger logger)
    {
        var dbContext = services.GetRequiredService<AppDbContext>();
        var userManager = services.GetRequiredService<UserManager<User>>();

        // "Can anyone actually sign in?", not "does any user row exist" and not "is there a
        // SystemAdmin". The Worker's DevDataSeeder creates a "System" user with Role = SystemAdmin
        // purely to own audit-trail foreign keys, and it has no password — a role-based or
        // table-wide guard would see it and skip, leaving a deployment nobody can log into. Same
        // class of mistake as DevAuthSeeder's original guard (see CLAUDE.md's Known Constraints).
        if (await dbContext.Users.AnyAsync(u => u.PasswordHash != null))
        {
            return;
        }

        var user = new User
        {
            Name = "Setup Administrator",
            Email = Email,
            UserName = Email,
            Role = UserRole.SystemAdmin,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, Password);
        if (!result.Succeeded)
        {
            logger.LogError("Could not create the setup SystemAdmin: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        dbContext.AddAuditLog(user.Id, "UserCreated", nameof(User), user.Id,
            "Setup SystemAdmin created automatically because no account could sign in.", DateTime.UtcNow);
        await dbContext.SaveChangesAsync();

        // Warning, not Information: published credentials are live from here until a real SystemAdmin
        // exists, and that must not be lost in a busy log. The password itself is deliberately not
        // logged — it is in the README, so repeating it here would add exposure without adding
        // information.
        logger.LogWarning(
            "No account on this deployment could sign in, so the setup account {Email} was created with " +
            "the documented default password (see README). It is deactivated automatically as soon as you " +
            "create a real SystemAdmin under Admin -> Users — do that first.",
            Email);
    }
}
