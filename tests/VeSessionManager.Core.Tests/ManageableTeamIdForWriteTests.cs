using VeSessionManager.Core.Authorization;
using VeSessionManager.Core.Entities;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Issue #263. <see cref="AdminAccessScope.TryResolveManageableTeamId"/> falls back to the acting
/// user's first team when they ask for one they do not manage, rather than refusing.
///
/// <para><b>That fallback is right for rendering and wrong for writing.</b> On a GET it means a stale
/// or hand-edited <c>?teamId=</c> lands you on a team you can actually see, which beats an error
/// page. On a POST it means the write silently goes to a <i>different team than the URL named</i> —
/// and the redirect afterwards reflects the substitution only once it has already happened.</para>
///
/// <para>No cross-tenant access results: the resolved team is always one the user manages. The harm
/// is confusion with teeth — a multi-team TeamAdmin following a wrong link overwrites Team X's Square
/// access token believing they are editing Team Y. This is exactly the confusion the
/// <c>availableTeamIds is { Count: 1 }</c> guard was already added to avoid for SystemAdmins.</para>
///
/// <para>So the write path gets a resolver that refuses instead of substituting. Both remain, because
/// both behaviors are wanted — the bug was having only one.</para>
/// </summary>
public class ManageableTeamIdForWriteTests
{
    private static AdminAccessScope CreateScope() => new(new SessionAccessScope());

    private static User TeamAdminOn(params int[] teamIds)
    {
        var user = new User { Id = 1, Name = "Team Admin", Role = UserRole.TeamAdmin };
        foreach (var teamId in teamIds)
        {
            user.UserTeams.Add(new UserTeam { UserId = user.Id, TeamId = teamId });
        }

        return user;
    }

    private static User SystemAdmin() => new() { Id = 2, Name = "Sys Admin", Role = UserRole.SystemAdmin };

    // ---- The regression --------------------------------------------------------------------

    /// <summary>
    /// The case #263 is about: a team the acting user does not manage must produce "no", not
    /// "here is a different one".
    /// </summary>
    [Fact]
    public async Task ATeamAdminAskingForATeamTheyDoNotManage_IsRefused_NotSilentlyRetargeted()
    {
        var scope = CreateScope();
        var user = TeamAdminOn(7, 9);

        var forWrite = scope.TryResolveManageableTeamIdForWrite(user, requestedTeamId: 42, [7, 9]);
        Assert.Null(forWrite);

        // The read-path resolver still substitutes, deliberately — asserted so the difference is a
        // decision on the record rather than an accident of which one a caller reached for.
        var forRead = scope.TryResolveManageableTeamId(user, requestedTeamId: 42, [7, 9]);
        Assert.Equal(7, forRead);

        await Task.CompletedTask;
    }

    // ---- Everything that must keep working --------------------------------------------------

    [Fact]
    public void ATeamAdminAskingForATeamTheyDoManage_Succeeds()
    {
        Assert.Equal(9, CreateScope().TryResolveManageableTeamIdForWrite(TeamAdminOn(7, 9), 9, [7, 9]));
    }

    /// <summary>
    /// One team and no explicit choice is unambiguous — the single-team admin, which is most of them,
    /// must not be made to pass a team id they have no way of choosing wrongly.
    /// </summary>
    [Fact]
    public void ATeamAdminWithExactlyOneTeam_AndNoRequest_GetsThatTeam()
    {
        Assert.Equal(7, CreateScope().TryResolveManageableTeamIdForWrite(TeamAdminOn(7), null, [7]));
    }

    /// <summary>
    /// Several teams and no explicit choice is ambiguous, and a write must not guess. Same rule the
    /// SystemAdmin branch already applied.
    /// </summary>
    [Fact]
    public void ATeamAdminWithSeveralTeams_AndNoRequest_IsRefused()
    {
        Assert.Null(CreateScope().TryResolveManageableTeamIdForWrite(TeamAdminOn(7, 9), null, [7, 9]));
    }

    [Fact]
    public void ASystemAdmin_MayWriteToAnyRequestedTeam()
    {
        Assert.Equal(42, CreateScope().TryResolveManageableTeamIdForWrite(SystemAdmin(), 42, [7, 9]));
    }

    [Fact]
    public void ASystemAdminWithNoRequest_GetsTheOnlyTeamIfThereIsOne_AndNothingOtherwise()
    {
        var scope = CreateScope();
        Assert.Equal(7, scope.TryResolveManageableTeamIdForWrite(SystemAdmin(), null, [7]));
        Assert.Null(scope.TryResolveManageableTeamIdForWrite(SystemAdmin(), null, [7, 9]));
        Assert.Null(scope.TryResolveManageableTeamIdForWrite(SystemAdmin(), null, null));
    }

    [Fact]
    public void AUserWithNoTeamsAtAll_IsRefused()
    {
        Assert.Null(CreateScope().TryResolveManageableTeamIdForWrite(TeamAdminOn(), 7, []));
        Assert.Null(CreateScope().TryResolveManageableTeamIdForWrite(TeamAdminOn(), null, []));
    }
}
