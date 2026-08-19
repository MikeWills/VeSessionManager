using System.Text.RegularExpressions;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Reads ARRL's confirmation page and decides whether a submission actually landed (issue #197).
///
/// <para><b>Success is recognized. Failure is not, deliberately.</b> ARRL's page states that it shows
/// "a failure message when unsuccessful", but nobody on this team has ever seen one — years of filing
/// by hand, no sample — and the only way to obtain one is to make a real bad submission to the
/// organization that issues licenses. A matcher built from zero samples would be guessing, and it
/// would guess in the expensive direction: a real rejection classified as handled, marked
/// <c>Submitted</c>, and never filed.</para>
///
/// <para>So there is exactly one positive signal — <b>the filename we posted</b> echoed back with
/// <c>has been uploaded successfully</c> — and everything else is
/// <see cref="ArrlReceiptOutcome.Unknown"/>, which goes to a human. Never a retry: a fire-and-forget
/// form POST supports no idempotency key, ARRL cannot dedupe, and a timeout after the request left
/// the machine may mean it succeeded.</para>
///
/// <para><b>Status codes are not consulted at all.</b> Both outcomes come back on the same endpoint,
/// and this codebase has already been bitten by reading an HTTP status as an answer — ExamTools'
/// login returns 200 with an error body.</para>
/// </summary>
public static class ArrlReceipt
{
    /// <summary>
    /// ARRL's wording, verified against two real receipts from 2026-04-21.
    ///
    /// <para><b>The filename and the phrase are not adjacent in the raw HTML</b> — the real page wraps
    /// the name in <c>&lt;b&gt;</c>, so <c>"{name} has been uploaded successfully"</c> as one literal
    /// never matches. The gap is matched as "markup and whitespace, but no other text", which is what
    /// keeps this from confirming a name mentioned three paragraphs earlier.</para>
    /// </summary>
    private const string SuccessPhrase = "has been uploaded successfully";

    /// <summary>
    /// Reads the response body against the filenames actually posted.
    /// </summary>
    /// <param name="body">ARRL's raw response. Stored verbatim elsewhere; only read here.</param>
    /// <param name="postedFileNames">
    /// Every file in the request. <b>All of them must be confirmed.</b> Whether ARRL prints one
    /// success line per file is unverified — no two-file sample exists — so a partial match is
    /// Unknown rather than success.
    /// </param>
    public static ArrlReceiptResult Read(string? body, IReadOnlyCollection<string> postedFileNames)
    {
        // Posting nothing cannot succeed, whatever the page says — otherwise "no files" would match
        // any page containing the phrase.
        if (string.IsNullOrWhiteSpace(body) || postedFileNames.Count == 0)
        {
            return new ArrlReceiptResult(ArrlReceiptOutcome.Unknown, [.. postedFileNames]);
        }

        var unconfirmed = postedFileNames.Where(name => !IsConfirmed(body, name)).ToList();

        return new ArrlReceiptResult(
            unconfirmed.Count == 0 ? ArrlReceiptOutcome.Succeeded : ArrlReceiptOutcome.Unknown,
            unconfirmed);
    }

    private static bool IsConfirmed(string body, string fileName)
    {
        // Between the name and the phrase: tags and whitespace only. Anything else means the two are
        // not part of the same statement, and the name is merely echoed in the summary block that
        // appears on every page.
        var pattern = Regex.Escape(fileName) + @"(?:\s|<[^>]*>)*" + Regex.Escape(SuccessPhrase);

        return Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    }
}

/// <param name="UnconfirmedFileNames">
/// What did not come back confirmed — the whole investigation when something goes wrong, and the
/// reason the outcome is Unknown rather than a bare "no".
/// </param>
public sealed record ArrlReceiptResult(ArrlReceiptOutcome Outcome, IReadOnlyList<string> UnconfirmedFileNames);

public enum ArrlReceiptOutcome
{
    /// <summary>Every posted filename came back confirmed. The only state that may mark a session Submitted.</summary>
    Succeeded = 0,

    /// <summary>
    /// Anything else. <b>Not "rejected"</b> — the submission may well have landed, and absence of a
    /// receipt is not absence of a filing. Surface it loudly and let a human check with ARRL.
    /// </summary>
    Unknown = 1
}
