namespace VeSessionManager.Core.Entities;

public class JobRunHistory
{
    public int Id { get; set; }
    public required string JobName { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
