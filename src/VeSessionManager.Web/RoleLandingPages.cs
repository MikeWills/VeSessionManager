using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Where a signed-in user lands after login. Phase 9a scaffolded one placeholder page per role;
/// as of Phase 9d, SystemAdmin and TeamAdmin land on the same real Sessions list SessionManager
/// does (that page's own [Authorize] already treats both as a superset of SessionManager for
/// session visibility — see SessionAccessScope), rather than a separate stub with no real content.
/// TeamLead still has no real view built (see TODO.md), so it keeps its own placeholder page.
/// </summary>
public static class RoleLandingPages
{
    public static string GetPath(UserRole role) => role switch
    {
        UserRole.SystemAdmin => "/SessionManager/Index",
        UserRole.TeamAdmin => "/SessionManager/Index",
        UserRole.SessionManager => "/SessionManager/Index",
        UserRole.TeamLead => "/TeamLead/Index",
        _ => "/Index"
    };
}
