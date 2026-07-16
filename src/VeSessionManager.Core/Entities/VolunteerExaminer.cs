namespace VeSessionManager.Core.Entities;

public class VolunteerExaminer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? CallSign { get; set; }
    public string? Frn { get; set; }

    public List<SessionVolunteerExaminer> SessionVolunteerExaminers { get; } = [];
}
