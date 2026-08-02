using System.Security.Cryptography;
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
        var suppliedPassword = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        var generated = string.IsNullOrWhiteSpace(suppliedPassword);
        var password = generated ? GeneratePassword() : suppliedPassword!;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine($"Usage: dotnet VeSessionManager.Web.dll {Switch} --email <email> --name <name> [--callsign <call>]");
            Console.Error.WriteLine($"       A password is generated and printed. Set {PasswordEnvironmentVariable} to choose your own.");
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

        Console.WriteLine($"Created SystemAdmin '{name}' <{email}>.");
        if (generated)
        {
            // Printed once, to this terminal only. Nothing writes it anywhere else, so if it is lost
            // the recovery is to create another administrator with this same switch.
            Console.WriteLine();
            Console.WriteLine($"  Password: {password}");
            Console.WriteLine();
            Console.WriteLine("  Shown once and stored only as a hash — save it now. If you lose it, run this");
            Console.WriteLine("  command again with a different email to create another administrator.");
        }

        Console.WriteLine("Sign in at /Account/Login, then add any further users under Admin -> Users.");
        return 0;
    }

    /// <summary>
    /// Satisfies Program.cs's Identity policy (12+ characters, and Identity's default
    /// digit/upper/lower requirements) by construction rather than by chance, then shuffles so the
    /// character classes are not in a fixed order. Non-alphanumerics are left out on purpose, and
    /// so are 0/O/1/l/I: this gets copied out of a terminal by hand at the one moment the operator
    /// has no other way in, and shell quoting or a misread glyph is an unrecoverable-feeling failure.
    /// </summary>
    private static string GeneratePassword()
    {
        const string Lower = "abcdefghijkmnopqrstuvwxyz";
        const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string Digits = "23456789";

        var characters = new List<char>
        {
            RandomNumberGenerator.GetString(Lower, 1)[0],
            RandomNumberGenerator.GetString(Upper, 1)[0],
            RandomNumberGenerator.GetString(Digits, 1)[0]
        };
        characters.AddRange(RandomNumberGenerator.GetString(Lower + Upper + Digits, 21));
        return new string([.. characters.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))]);
    }

    private static string? ArgumentValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
