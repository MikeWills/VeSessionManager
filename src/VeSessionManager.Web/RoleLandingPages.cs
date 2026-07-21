using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>Where a signed-in user lands after login — one placeholder page per role (Phase 9a).</summary>
public static class RoleLandingPages
{
    public static string GetPath(UserRole role) => role switch
    {
        UserRole.SystemAdmin => "/SystemAdmin/Index",
        UserRole.TeamAdmin => "/TeamAdmin/Index",
        UserRole.SessionManager => "/SessionManager/Index",
        UserRole.TeamLead => "/TeamLead/Index",
        _ => "/Index"
    };
}
