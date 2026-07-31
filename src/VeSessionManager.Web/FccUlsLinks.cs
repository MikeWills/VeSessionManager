namespace VeSessionManager.Web;

/// <summary>
/// The one place FCC ULS deep links are built. Previously inlined in CandidateDetail; shared once
/// Applicant Status needed the same links (2026-07-31).
/// </summary>
public static class FccUlsLinks
{
    /// <summary>
    /// A granted licence's ULS record. **Verified shape** — ExamTools itself links to exactly this
    /// URL/param, and the licKey is the ULS "Unique System Identifier" stored in
    /// Candidate.FccUlsLicenseKey. Returns null when there is no key yet.
    /// </summary>
    public static string? License(string? fccUlsLicenseKey) =>
        string.IsNullOrWhiteSpace(fccUlsLicenseKey)
            ? null
            : $"https://wireless2.fcc.gov/UlsApp/UlsSearch/license.jsp?licKey={Uri.EscapeDataString(fccUlsLicenseKey)}";

    /// <summary>
    /// FCC's Application Search entry page — deliberately NOT a per-application deep link.
    ///
    /// <para>The `applView.jsp?applID=…`-shaped guess was left unshipped twice (2026-07-29 and again
    /// 2026-07-31) for the same reason: `wireless2.fcc.gov` returns Akamai "Access Denied" (HTTP 403)
    /// to automated requests *and* to at least one manual browser attempt, so the shape has never
    /// been confirmed against a working response. Shipping an unverified deep link would send a
    /// Session Manager to a dead page with no way to tell whether the application is missing or the
    /// URL is simply wrong. The application file number is rendered next to this link instead, for
    /// paste-in lookup — same fallback the FRN column already provides.</para>
    ///
    /// <para>To close this properly: observe a working ULS application URL (from a browser that can
    /// reach the site, or from ExamTools' own applicant link) and replace this with the real shape.
    /// See TODO.md.</para>
    /// </summary>
    public const string ApplicationSearch = "https://wireless2.fcc.gov/UlsApp/ApplicationSearch/searchAppl.jsp";
}
