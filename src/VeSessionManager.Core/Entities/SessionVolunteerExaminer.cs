namespace VeSessionManager.Core.Entities;

/// <summary>Join table tracking which VEs worked which sessions.</summary>
public class SessionVolunteerExaminer
{
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    public int VolunteerExaminerId { get; set; }
    public VolunteerExaminer VolunteerExaminer { get; set; } = null!;
}
