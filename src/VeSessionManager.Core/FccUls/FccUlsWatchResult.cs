namespace VeSessionManager.Core.FccUls;

public class FccUlsWatchResult
{
    public int CandidatesMarkedReceived { get; set; }
    public int CandidatesMarkedGranted { get; set; }
    public bool ApplicationFileAvailable { get; set; }
    public bool LicenseFileAvailable { get; set; }

    public override string ToString() =>
        $"received {CandidatesMarkedReceived}, granted {CandidatesMarkedGranted}, " +
        $"application file {(ApplicationFileAvailable ? "processed" : "unavailable")}, " +
        $"license file {(LicenseFileAvailable ? "processed" : "unavailable")}";
}
