using System.Net.Http.Headers;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.VecSubmissions;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Building the multipart body ARRL's form expects (issue #197).
///
/// <para><b>These field names are not ours to choose.</b> Every one was read out of the live form's
/// HTML on 2026-08-18; a rename on either side is a silent failure, since an unrecognized field is
/// simply ignored by a PHP form handler rather than rejected. That is why this asserts the wire names
/// rather than the C# property names, and why it never sends anything: the body is built as a value
/// and inspected here.</para>
/// </summary>
public class ArrlSubmissionRequestTests
{
    private static ArrlSubmissionPayload Payload(Action<ArrlSubmissionPayloadBuilder>? configure = null)
    {
        var builder = new ArrlSubmissionPayloadBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>Mutable stand-in so each test varies one field without repeating all nine.</summary>
    private sealed class ArrlSubmissionPayloadBuilder
    {
        public string FullName { get; set; } = "Mike Wills";
        public string CallSign { get; set; } = "WX0MIK";
        public string Email { get; set; } = "wx0mik@gmail.com";
        public string Phone { get; set; } = "5073814969";
        public string SessionDate { get; set; } = "2026-04-21";
        public string Location { get; set; } = "Remote Online";
        public ArrlPaymentMethod PaymentMethod { get; set; } = ArrlPaymentMethod.CreditCardOnFile;
        public string AmountCharged { get; set; } = "8.00";
        public string? Note { get; set; }
        public List<ArrlSubmissionFile> Files { get; set; } =
            [new("ExamSession_MARC_20260422_0130_arrl.zip", [0x50, 0x4B, 0x03, 0x04])];

        public ArrlSubmissionPayload Build() => new()
        {
            FullName = FullName, CallSign = CallSign, Email = Email, Phone = Phone,
            SessionDate = SessionDate, Location = Location, PaymentMethod = PaymentMethod,
            AmountCharged = AmountCharged, Note = Note, Files = Files
        };
    }

    private static async Task<Dictionary<string, string>> ReadFieldsAsync(MultipartFormDataContent content)
    {
        var fields = new Dictionary<string, string>();
        foreach (var part in content)
        {
            var name = part.Headers.ContentDisposition?.Name?.Trim('"');
            if (name is null || part.Headers.ContentDisposition?.FileName is not null)
            {
                continue;
            }

            fields[name] = await part.ReadAsStringAsync();
        }

        return fields;
    }

    private static string? Unquote(string? value) => value?.Trim('"');

    private static List<ContentDispositionHeaderValue> ReadFileParts(MultipartFormDataContent content) =>
        [.. content
            .Select(p => p.Headers.ContentDisposition)
            .Where(d => d?.FileName is not null)
            .Select(d => d!)];

    [Fact]
    public async Task EveryTextFieldUsesArrlsOwnName()
    {
        var fields = await ReadFieldsAsync(ArrlSubmissionRequest.Build(Payload()));

        Assert.Equal("Mike Wills", fields["fullname"]);
        Assert.Equal("WX0MIK", fields["callsign"]);
        Assert.Equal("wx0mik@gmail.com", fields["email"]);
        Assert.Equal("5073814969", fields["phone"]);
        Assert.Equal("2026-04-21", fields["sessionDate"]);
        Assert.Equal("Remote Online", fields["location"]);
        Assert.Equal("8.00", fields["amountCharged"]);
    }

    /// <summary>
    /// The radio's values, exactly as the form posts them. An unrecognized value is not rejected — it
    /// arrives as a payment method ARRL does not recognize, on a filing that otherwise looks fine.
    /// </summary>
    [Theory]
    [InlineData(ArrlPaymentMethod.MailIn, "mail-in")]
    [InlineData(ArrlPaymentMethod.PhoneIn, "phone-in")]
    [InlineData(ArrlPaymentMethod.CreditCardOnFile, "credit-card-filed")]
    public async Task ThePaymentMethodPostsArrlsOwnValue(ArrlPaymentMethod method, string expected)
    {
        var fields = await ReadFieldsAsync(ArrlSubmissionRequest.Build(Payload(p => p.PaymentMethod = method)));

        Assert.Equal(expected, fields["paymentMethod"]);
    }

    /// <summary>
    /// The submit button's own name/value pair. A browser sends it and a PHP handler may well branch
    /// on it, so leaving it out risks a request that differs from a real one in a way nobody could
    /// diagnose from this side.
    /// </summary>
    [Fact]
    public async Task TheSubmitButtonIsIncluded()
    {
        var fields = await ReadFieldsAsync(ArrlSubmissionRequest.Build(Payload()));

        Assert.Equal("Upload!", fields["submit-btn"]);
    }

    /// <summary>
    /// The file input is <c>the_upload[]</c> — an array name, brackets included. Posting it as
    /// <c>the_upload</c> would give PHP a single value where the handler expects an array, which is
    /// exactly the sort of near-miss that fails only for the two-file case.
    /// </summary>
    [Fact]
    public void FilesArePostedUnderTheArrayName()
    {
        var files = ReadFileParts(ArrlSubmissionRequest.Build(Payload()));

        var part = Assert.Single(files);
        // Quotes around a header value are a serialization detail, so they are trimmed rather than
        // asserted — the name and the filename are what ARRL's handler reads.
        Assert.Equal("the_upload[]", Unquote(part.Name));
        Assert.Equal("ExamSession_MARC_20260422_0130_arrl.zip", Unquote(part.FileName));
    }

    /// <summary>Both files go in one request — the archive and the youth grant form — under the same repeated array name.</summary>
    [Fact]
    public void TwoFilesArePostedInOneRequest()
    {
        var payload = Payload(p => p.Files.Add(new ArrlSubmissionFile("youth-grant-form.pdf", [1, 2, 3])));

        var files = ReadFileParts(ArrlSubmissionRequest.Build(payload));

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal("the_upload[]", Unquote(f.Name)));
        Assert.Contains(files, f => Unquote(f.FileName) == "youth-grant-form.pdf");
    }

    /// <summary>An empty note is a legitimate submission — MARC's real receipt shows an empty Notes field — so the part is still sent rather than omitted.</summary>
    [Fact]
    public async Task AnEmptyNoteIsStillSent()
    {
        var fields = await ReadFieldsAsync(ArrlSubmissionRequest.Build(Payload(p => p.Note = null)));

        Assert.True(fields.ContainsKey("note"));
        Assert.Equal("", fields["note"]);
    }

    [Fact]
    public async Task ANoteIsSentVerbatim()
    {
        var note = "Bill credit card ending in 4973 on file for N1CCK";

        var fields = await ReadFieldsAsync(ArrlSubmissionRequest.Build(Payload(p => p.Note = note)));

        Assert.Equal(note, fields["note"]);
    }

    /// <summary>Posting no file at all is a programming error, not a submission ARRL should be asked to interpret.</summary>
    [Fact]
    public void BuildingWithNoFiles_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ArrlSubmissionRequest.Build(Payload(p => p.Files = [])));
    }

    /// <summary>ARRL's own cap. Refused here rather than discovered as a truncated or rejected upload.</summary>
    [Fact]
    public void MoreThanTwoFiles_Throws()
    {
        var payload = Payload(p =>
        {
            p.Files.Add(new ArrlSubmissionFile("b.pdf", [1]));
            p.Files.Add(new ArrlSubmissionFile("c.pdf", [1]));
        });

        Assert.Throws<InvalidOperationException>(() => ArrlSubmissionRequest.Build(payload));
    }
}
