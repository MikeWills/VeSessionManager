namespace VeSessionManager.Core.Uls;

public class UlsWatchResult
{
    public int CandidatesChecked { get; set; }
    public int CandidatesMarkedGranted { get; set; }

    /// <summary>Lookups that could not be performed at all (network/HTTP). Distinct from "FCC has no record" — those candidates simply stay non-terminal and are retried next run.</summary>
    public int LookupFailures { get; set; }

    public override string ToString() =>
        $"checked {CandidatesChecked}, granted {CandidatesMarkedGranted}, lookup failures {LookupFailures}";
}
