using VeSessionManager.Core.VecSubmissions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Reading ARRL's confirmation page (issue #197).
///
/// <para><b>Success is recognized; failure is deliberately not.</b> Mike has never seen a failure
/// page in years of filing by hand, so there is no sample of one and no way to obtain one that does
/// not involve making a real bad submission. A matcher built from zero samples would be guessing, and
/// it would guess in the expensive direction: a real rejection mistaken for a recognized one, marked
/// Submitted, and never filed.</para>
///
/// <para>So the only positive signal is <b>the filename we posted</b> coming back followed by
/// <c>has been uploaded successfully</c>. Everything else is <see cref="ArrlReceiptOutcome.Unknown"/>
/// and goes to a human — never a retry, because a fire-and-forget form POST supports no idempotency
/// key and ARRL cannot dedupe.</para>
/// </summary>
public class ArrlReceiptTests
{
    /// <summary>
    /// The real page, from a saved MARC receipt on #197. Reproduced as the markup ARRL's own template
    /// produces around the same text.
    /// </summary>
    private const string RealReceipt = """
        <html><body>
        <h3>Welcome to ARRL's VEC Upload Page</h3>
        <p><b>Here is a summary of the details submitted:</b><br />
        <b>Full Name:</b> Mike Wills<br />
        <b>Call Sign:</b> WX0MIK<br />
        <b>Notes:</b> <br />
        <b>Email:</b> wx0mik@gmail.com<br />
        <b>IP Address:</b> 174.199.106.160<br />
        <b>Phone:</b> 5073814969<br />
        <b>Exam Session Date:</b> 2026-04-21<br />
        <b>Exam Session Location:</b> Remote Online<br />
        <b>Method of Payment:</b> credit-card-filed<br />
        <b>Amount forwarded to ARRL VEC:</b> 8.00</p>
        <p><b>ExamSession_MARC_20260422_0130_arrl.zip</b> has been uploaded successfully.</p>
        <p><a href="?">Upload another file</a></p>
        </body></html>
        """;

    private const string ArchiveName = "ExamSession_MARC_20260422_0130_arrl.zip";

    [Fact]
    public void TheRealReceipt_IsRecognizedAsSuccess()
    {
        var result = ArrlReceipt.Read(RealReceipt, [ArchiveName]);

        Assert.Equal(ArrlReceiptOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.UnconfirmedFileNames);
    }

    /// <summary>
    /// The heart of it: the page names the file, so this matches <b>what we sent</b> rather than
    /// generic page copy. A receipt confirming somebody else's upload is not confirmation of ours.
    /// </summary>
    [Fact]
    public void AReceiptNamingADifferentFile_IsUnknown()
    {
        var result = ArrlReceipt.Read(RealReceipt, ["ExamSession_HRCC_20260422_0230_arrl.zip"]);

        Assert.Equal(ArrlReceiptOutcome.Unknown, result.Outcome);
        Assert.Contains("ExamSession_HRCC_20260422_0230_arrl.zip", result.UnconfirmedFileNames);
    }

    /// <summary>
    /// The two-file case — an archive plus the youth grant form. Whether ARRL prints one success line
    /// per file is <b>unverified</b>: there is no two-file sample. Until a real youth submission
    /// settles it, every posted filename must be confirmed before the whole thing counts as success.
    /// </summary>
    [Fact]
    public void WithTwoFilesAndOnlyOneConfirmed_TheResultIsUnknown()
    {
        var result = ArrlReceipt.Read(RealReceipt, [ArchiveName, "youth-grant-form.pdf"]);

        Assert.Equal(ArrlReceiptOutcome.Unknown, result.Outcome);
        Assert.Equal("youth-grant-form.pdf", Assert.Single(result.UnconfirmedFileNames));
    }

    [Fact]
    public void WithTwoFilesBothConfirmed_TheResultIsSuccess()
    {
        var receipt = RealReceipt.Replace(
            "<p><a href=\"?\">Upload another file</a></p>",
            "<p><b>youth-grant-form.pdf</b> has been uploaded successfully.</p>");

        var result = ArrlReceipt.Read(receipt, [ArchiveName, "youth-grant-form.pdf"]);

        Assert.Equal(ArrlReceiptOutcome.Succeeded, result.Outcome);
    }

    /// <summary>
    /// The filename and the phrase are separated by markup on the real page — <c>&lt;b&gt;</c> around
    /// the name — so the two are never adjacent in the raw HTML. Matching them as one literal string
    /// would fail against the only receipt anyone has.
    /// </summary>
    [Fact]
    public void TheFilenameAndThePhrase_MayBeSeparatedByMarkup()
    {
        var receipt = $"<p><b>{ArchiveName}</b> has been uploaded successfully.</p>";

        Assert.Equal(ArrlReceiptOutcome.Succeeded, ArrlReceipt.Read(receipt, [ArchiveName]).Outcome);
    }

    /// <summary>The name must be confirmed, not merely mentioned — it is echoed in the summary block on every page, success or not.</summary>
    [Fact]
    public void AFilenameMentionedWithoutTheSuccessPhrase_IsNotSuccess()
    {
        var receipt = $"<p>We received a file called {ArchiveName} but could not process it.</p>";

        Assert.Equal(ArrlReceiptOutcome.Unknown, ArrlReceipt.Read(receipt, [ArchiveName]).Outcome);
    }

    /// <summary>
    /// No failure detection, on purpose. Something that looks like a rejection is still Unknown, so it
    /// reaches a human rather than being classified by a rule nobody has ever validated against a real
    /// failure page.
    /// </summary>
    [Fact]
    public void SomethingThatLooksLikeAFailure_IsUnknownRatherThanRejected()
    {
        var receipt = "<p>Error: the upload could not be completed.</p>";

        var result = ArrlReceipt.Read(receipt, [ArchiveName]);

        Assert.Equal(ArrlReceiptOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public void AnEmptyOrMissingBody_IsUnknown()
    {
        Assert.Equal(ArrlReceiptOutcome.Unknown, ArrlReceipt.Read("", [ArchiveName]).Outcome);
        Assert.Equal(ArrlReceiptOutcome.Unknown, ArrlReceipt.Read(null, [ArchiveName]).Outcome);
    }

    /// <summary>Posting nothing cannot be a success, whatever the page says — otherwise an empty submission would "succeed" against any page containing the phrase.</summary>
    [Fact]
    public void WithNoFilesPosted_TheResultIsUnknown()
    {
        Assert.Equal(ArrlReceiptOutcome.Unknown, ArrlReceipt.Read(RealReceipt, []).Outcome);
    }

    /// <summary>ARRL's own casing is not a contract, and this is the cheap half of not depending on it.</summary>
    [Fact]
    public void ThePhraseIsMatchedCaseInsensitively()
    {
        var receipt = $"<p><b>{ArchiveName}</b> HAS BEEN UPLOADED SUCCESSFULLY.</p>";

        Assert.Equal(ArrlReceiptOutcome.Succeeded, ArrlReceipt.Read(receipt, [ArchiveName]).Outcome);
    }
}
