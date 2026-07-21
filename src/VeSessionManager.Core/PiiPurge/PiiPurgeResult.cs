namespace VeSessionManager.Core.PiiPurge;

public class PiiPurgeResult
{
    public int GrantedCandidatesPurged { get; set; }
    public int FailedCandidatesPurged { get; set; }

    public override string ToString() =>
        $"granted candidates purged {GrantedCandidatesPurged}, failed candidates purged {FailedCandidatesPurged}";
}
