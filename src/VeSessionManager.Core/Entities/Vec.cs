namespace VeSessionManager.Core.Entities;

public class Vec
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>ARRL's youth discount/FCC-fee-reimbursement scholarship program.</summary>
    public bool SupportsYouthProgram { get; set; }

    public string? Notes { get; set; }

    public List<FeeConfiguration> FeeConfigurations { get; } = [];
    public List<Session> Sessions { get; } = [];
}
