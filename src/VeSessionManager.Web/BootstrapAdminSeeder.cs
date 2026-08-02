using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Creates a temporary SystemAdmin on a deployment where **nobody can sign in at all**, so a fresh
/// server is usable without hand-editing the database. Intended to be disabled the moment a real
/// account exists — Admin → Users → Deactivate.
///
/// **The password is randomly generated per deployment and printed once.** It is deliberately not a
/// constant: a fixed default (the shape `DevAuthSeeder.DevPassword` uses, which is fine for a
/// throwaway dev fixture published in the README) would mean every deployment of this app shipped
/// with the same known credentials on an internet-facing login page. That is the classic
/// default-credential vulnerability, and it is worth the small inconvenience to avoid.
///
/// **Trade-off worth knowing:** the generated password is written to the log, which is the one place
/// this codebase otherwise never puts a credential. That is accepted here because the alternative —
/// an account with a predictable password — is worse, and because the account is expected to live
/// for minutes. `BootstrapAdminCommand` (`--create-admin`) is the stricter option and takes its
/// password from the environment, so it never touches the log at all; prefer it where the operator
/// has shell access anyway. See docs/deployment.md.
/// </summary>
public static class BootstrapAdminSeeder
{
    public const string Email = "setup@vesessionmanager.local";

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

        var password = GeneratePassword();
        var user = new User
        {
            Name = "Setup Administrator",
            Email = Email,
            UserName = Email,
            Role = UserRole.SystemAdmin,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            logger.LogError("Could not create the bootstrap SystemAdmin: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        dbContext.AddAuditLog(user.Id, "UserCreated", nameof(User), user.Id,
            "Temporary bootstrap SystemAdmin created automatically because no account could sign in.", DateTime.UtcNow);
        await dbContext.SaveChangesAsync();

        // Warning, not Information: this is a standing security exposure until someone acts on it,
        // and it must not be lost in a busy log.
        logger.LogWarning(
            "No account on this deployment could sign in, so a TEMPORARY SystemAdmin was created.\n" +
            "    Email:    {Email}\n" +
            "    Password: {Password}\n" +
            "  Sign in, create your own account under Admin -> Users, then DEACTIVATE this one. " +
            "It keeps working — and this password stays valid — until you do.",
            Email, password);
    }

    /// <summary>
    /// Satisfies Program.cs's Identity policy (12+ chars, and Identity's default digit/upper/lower
    /// requirements) by construction rather than by chance, then shuffles so the character classes
    /// aren't in a predictable order. Non-alphanumerics are omitted on purpose — the password gets
    /// copied out of a terminal, and quoting rules are an easy way to lose someone at the one step
    /// where they cannot recover by themselves.
    /// </summary>
    private static string GeneratePassword()
    {
        const string Lower = "abcdefghijkmnopqrstuvwxyz";  // no l
        const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // no I, O
        const string Digits = "23456789";                  // no 0, 1

        var characters = new List<char>
        {
            RandomNumberGenerator.GetString(Lower, 1)[0],
            RandomNumberGenerator.GetString(Upper, 1)[0],
            RandomNumberGenerator.GetString(Digits, 1)[0]
        };
        characters.AddRange(RandomNumberGenerator.GetString(Lower + Upper + Digits, 21));
        return new string([.. characters.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))]);
    }
}
