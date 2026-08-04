namespace VeSessionManager.Core.PiiPurge;

public class PiiPurgeResult
{
    public int GrantedCandidatesPurged { get; set; }
    public int FailedCandidatesPurged { get; set; }

    /// <summary>Already-purged rows re-cleared because a field was added to the purge definition after they were purged (see PiiPurgeService.RepairIncompletelyPurgedCandidatesAsync). Expected to be non-zero once, then always zero.</summary>
    public int AlreadyPurgedCandidatesRepaired { get; set; }

    public override string ToString() =>
        $"granted candidates purged {GrantedCandidatesPurged}, failed candidates purged {FailedCandidatesPurged}, previously-purged repaired {AlreadyPurgedCandidatesRepaired}";
}
