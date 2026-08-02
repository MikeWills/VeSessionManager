using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// One-off `--create-admin` switch that creates the first SystemAdmin on a fresh deployment.
///
/// **Why this has to exist.** `DevAuthSeeder` only runs in Development, so a Production database
/// starts with an empty `AspNetUsers` table — and every route into the admin UI that could create a
/// user is itself `[Authorize]`d. Nobody can sign in, so nobody can create the account that would
/// let them sign in. Before this, the documented answer was a hand-written SQL insert, which is not
/// really viable: `PasswordHash` has to be produced by `PasswordHasher&lt;User&gt;`, and
/// `SecurityStamp`/`NormalizedEmail`/`NormalizedUserName` all have to be right or sign-in fails in
/// ways that look like a wrong password.
///
/// Lives in the Web project rather than beside the Worker's other one-off switches because
/// `UserManager&lt;User&gt;` — the thing that hashes the password — is only registered here.
///
/// The password is read from the **`VSM_ADMIN_PASSWORD` environment variable**, never a command-line
/// argument: arguments are visible in shell history and to any user who can run `ps`. It is never
/// logged, and never echoed back.
///
/// Usage on the server:
/// <code>
/// VSM_ADMIN_PASSWORD='...' dotnet VeSessionManager.Web.dll --create-admin \
///     --email admin@example.org --name "Mike Wills" [--callsign WX0MIK]
/// </code>
/// </summary>
public static class BootstrapAdminCommand
{
    public const string Switch = "--create-admin";
    public const string PasswordEnvironmentVariable = "VSM_ADMIN_PASSWORD";

    /// <summary>Returns an exit code: 0 on success, 1 on any usage or validation failure.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var email = ArgumentValue(args, "--email");
        var name = ArgumentValue(args, "--name");
        var callSign = ArgumentValue(args, "--callsign");
        var password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine($"Usage: {PasswordEnvironmentVariable}='...' dotnet VeSessionManager.Web.dll {Switch} --email <email> --name <name> [--callsign <call>]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine($"{PasswordEnvironmentVariable} is not set. Set it in the environment rather than passing the password as an argument, so it stays out of shell history and `ps` output.");
            return 1;
        }

        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        // Migrations have not necessarily run yet on a brand-new box — this switch is the very first
        // thing an operator does, quite possibly before the services have ever started.
        await dbContext.Database.MigrateAsync();

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            Console.Error.WriteLine($"A user with email {email} already exists — nothing to do.");
            return 1;
        }

        var user = new User
        {
            Name = name,
            Email = email,
            UserName = email,
            CallSign = string.IsNullOrWhiteSpace(callSign) ? null : callSign.ToUpperInvariant(),
            Role = UserRole.SystemAdmin,
            // No email-confirmation infrastructure exists (Program.cs sets RequireConfirmedAccount
            // false); marking it confirmed keeps this account consistent with every other one.
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine("Could not create the account:");
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"  - {error.Description}");
            }

            return 1;
        }

        // Self-attributed: there is no other user in existence to credit, and leaving the bootstrap
        // of a SystemAdmin account entirely unrecorded would be worse.
        dbContext.AddAuditLog(user.Id, "UserCreated", nameof(User), user.Id,
            $"Bootstrap SystemAdmin {user.Id} created via {Switch}.", DateTime.UtcNow);
        await dbContext.SaveChangesAsync();

        var existingAdmins = await dbContext.Users.CountAsync(u => u.Role == UserRole.SystemAdmin);
        Console.WriteLine($"Created SystemAdmin '{name}' <{email}>. This deployment now has {existingAdmins} SystemAdmin account(s).");
        Console.WriteLine("Sign in at /Account/Login, then create any further users through Admin -> Users.");
        return 0;
    }

    private static string? ArgumentValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
