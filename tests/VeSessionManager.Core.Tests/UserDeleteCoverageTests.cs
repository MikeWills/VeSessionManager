using Microsoft.EntityFrameworkCore;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Every foreign key pointing at <see cref="User"/> is accounted for by
/// <c>UserManagementService.DeleteAsync</c> (#188).
///
/// <para><b>What goes wrong without this.</b> All of those keys are <c>Restrict</c>. A key added
/// later and not checked does not make the delete permissive — it makes it throw a constraint
/// violation at an admin, at the end of an operation that already looked like it was going to work.
/// The refusal path exists precisely so the answer is a sentence instead of a 500.</para>
///
/// <para>Read from EF's model rather than a hand-kept list, so adding a key is what fails the build,
/// not remembering to update a list. Same shape as <c>JobCoverageCompletenessTests</c>.</para>
/// </summary>
public class UserDeleteCoverageTests
{
    /// <summary>
    /// Every FK to User, as "EntityType.ForeignKeyProperty", and what the delete does about it.
    ///
    /// <para>Three are deliberately <b>not</b> blockers: <c>UserTeam</c> is the account's own
    /// membership configuration and is removed with it; <c>AuditLog</c> is split — rows <i>about</i>
    /// this user go with it, rows where it <i>acted</i> block — and that split is asserted by
    /// behavior in <c>UserDeleteTests</c>, not here.</para>
    /// </summary>
    private static readonly HashSet<string> Accounted =
    [
        "FeeConfiguration.CreatedByUserId",
        "Session.TestingCompletedByUserId",
        "Session.VecSubmittedByUserId",
        "Session.RetainedAmountOverrideByUserId",
        "Candidate.ResultMarkedByUserId",
        "Payment.RefundRequestedByUserId",
        "Refund.RequestedByUserId",
        "HistoricalImportRequest.RequestedByUserId",
        "EmailTemplate.UpdatedByUserId",
        "EmailSettings.UpdatedByUserId",
        "SystemSettings.UpdatedByUserId",
        "UnmatchedSquarePayment.ResolvedByUserId",
        "WatchedLicense.AddedByUserId",
        "User.ManagedByUserId",
        "AuditLog.UserId",
        "UserTeam.UserId",

        // Identity's own tables, and the reason this guard is worth having: they were not on the
        // fourteen-FK list #188 enumerated, and this test found them on its first run. They need no
        // blocker — userManager.DeleteAsync removes them, which is the "covers the Identity side
        // (logins/claims/tokens)" the issue already noted. No roles table: this app derives from
        // IdentityUserContext and keeps the role as a User.Role enum, which the stale-entry check
        // below established by rejecting a speculative IdentityUserRole entry — but "handled elsewhere" and
        // "forgotten" look identical until something says which.
        "IdentityUserClaim<int32>.UserId",
        "IdentityUserLogin<int32>.UserId",
        "IdentityUserToken<int32>.UserId"
    ];

    [Fact]
    public void EveryForeignKeyToUserIsAccountedForByTheDeletePath()
    {
        using var dbContext = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var found = new List<string>();
        foreach (var entityType in dbContext.Model.GetEntityTypes())
        {
            foreach (var fk in entityType.GetForeignKeys())
            {
                if (fk.PrincipalEntityType.ClrType != typeof(User))
                {
                    continue;
                }

                foreach (var property in fk.Properties)
                {
                    // Generic names arrive as "IdentityUserClaim`1"; rendered readably so the
                    // allow-list and a failure message both name the type the way a person would.
                    var typeName = entityType.ClrType.Name.Split('`')[0];
                    if (entityType.ClrType.IsGenericType)
                    {
                        typeName += "<" + string.Join(", ", entityType.ClrType.GetGenericArguments().Select(a => a.Name.ToLowerInvariant())) + ">";
                    }

                    found.Add($"{typeName}.{property.Name}");
                }
            }
        }

        var unaccounted = found.Where(f => !Accounted.Contains(f)).Distinct().OrderBy(f => f).ToList();

        Assert.True(unaccounted.Count == 0,
            "These foreign keys point at User and DeleteAsync does nothing about them, so deleting an "
            + "account they reference will throw a Restrict violation instead of refusing with a reason:\n  "
            + string.Join("\n  ", unaccounted)
            + "\n\nAdd a blocker in UserManagementService.FindDeleteBlockersAsync (or handle the rows "
            + "explicitly, as UserTeam and the lifecycle audit rows are), then list it here.");

        // The other direction: an entry here that no longer matches a real key is a stale allowance,
        // and would quietly excuse a key that gets renamed into it.
        var stale = Accounted.Where(a => !found.Contains(a)).OrderBy(a => a).ToList();
        Assert.True(stale.Count == 0,
            "These are listed as accounted for but no such foreign key exists any more:\n  " + string.Join("\n  ", stale));
    }
}
