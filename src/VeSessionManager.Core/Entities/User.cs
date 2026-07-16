namespace VeSessionManager.Core.Entities;

/// <summary>
/// Plain admin-backend user record matching the shared data model. Not ASP.NET Core Identity
/// yet — that integration (username/password + Google/Microsoft/Apple sign-in) is Phase 9a's
/// job, so don't assume authentication exists on top of this yet.
/// </summary>
public class User
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public UserRole Role { get; set; }

    /// <summary>TeamLead's assigned SessionManager.</summary>
    public int? ManagedByUserId { get; set; }
    public User? ManagedByUser { get; set; }
}
