namespace VeSessionManager.Core.Uls;

/// <summary>
/// Read-only client for ExamTools' public ULS mirror. Wrapped in an interface so UlsWatcherService
/// can be unit tested without live calls, same as IExamToolsClient/IFccUlsClient before it.
/// </summary>
public interface IUlsLookupClient
{
    /// <summary>
    /// One FRN's current ULS state. Returns <see cref="UlsLookupResult.NotFound"/> when the endpoint
    /// reports <c>type: "notfound"</c> (an FRN FCC has no record of — normal for a candidate whose
    /// application hasn't been filed yet), and <c>null</c> when the lookup could not be performed at
    /// all (network/HTTP failure). Callers must treat those two differently: not-found means "no
    /// change", null means "we learned nothing, try again next run".
    /// </summary>
    Task<UlsLookupResult?> LookupByFrnAsync(string frn, CancellationToken cancellationToken);
}
