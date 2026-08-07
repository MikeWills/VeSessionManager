namespace VeSessionManager.Web;

/// <summary>
/// The one place FCC ULS deep links are built. Previously inlined in CandidateDetail; shared once
/// Applicant Status needed the same links (2026-07-31).
/// </summary>
public static class FccUlsLinks
{
    /// <summary>
    /// A granted license's ULS record. **Verified shape** — ExamTools itself links to exactly this
    /// URL/param, and the licKey is the ULS "Unique System Identifier" stored in
    /// Candidate.FccUlsLicenseKey. Returns null when there is no key yet.
    /// </summary>
    public static string? License(string? fccUlsLicenseKey) =>
        string.IsNullOrWhiteSpace(fccUlsLicenseKey)
            ? null
            : $"https://wireless2.fcc.gov/UlsApp/UlsSearch/license.jsp?licKey={Uri.EscapeDataString(fccUlsLicenseKey)}";

    // No application deep link, deliberately — investigated to a conclusion 2026-07-31 and closed:
    //  1. FCC's Application Search results page is session-scoped
    //     (`results.jsp?applSearchKey=applSearchKey20266311340484`), so there is no stable URL to
    //     build even with full access.
    //  2. `wireless2.fcc.gov/UlsApp/ApplicationSearch/*` returns Akamai 403 to this deployment's
    //     operator, including from other VPN exits — so any link would land on an error page.
    //  3. An application record's own USI (the plausible key) is not exposed by the ULS lookup API
    //     at all; only `uls_filenumber` is, which is stored on Candidate for reference.
    // `UlsSearch/license.jsp?licKey=` above is unaffected and verified working. See docs/uls-watcher.md.
}
