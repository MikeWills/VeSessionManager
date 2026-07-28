namespace VeSessionManager.Core.Entities;

/// <summary>Join table tracking which Teams a User (TeamAdmin/SessionManager) belongs to — replaces
/// the old single, nullable User.TeamId now that a user can work with more than one team. See
/// docs/admin-auth.md.</summary>
public class UserTeam
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public DateTime CreatedUtc { get; set; }
}
