using System.Net.Http.Headers;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// One file in an ARRL submission — the VEC archive, or the youth grant program form beside it.
/// </summary>
public sealed record ArrlSubmissionFile(string FileName, byte[] Content);

/// <summary>
/// Exactly what would be posted to ARRL, as a value.
///
/// <para>Every field is a string because <b>every field is editable on the preview</b>: the screen is
/// the source of truth for a submission, not the team configuration it was prefilled from. That is
/// also why the archive stores these values rather than re-deriving them later — the record has to
/// say what was filed, not what configuration would produce today.</para>
/// </summary>
public sealed record ArrlSubmissionPayload
{
    public required string FullName { get; init; }
    public required string CallSign { get; init; }
    public required string Email { get; init; }
    public required string Phone { get; init; }

    /// <summary>ISO <c>yyyy-MM-dd</c> — ARRL's field is an <c>&lt;input type="date"&gt;</c>.</summary>
    public required string SessionDate { get; init; }

    public required string Location { get; init; }
    public required ArrlPaymentMethod PaymentMethod { get; init; }

    /// <summary>Plain decimal, no currency symbol: <c>8.00</c>, as the real receipts show.</summary>
    public required string AmountCharged { get; init; }

    public string? Note { get; init; }

    public required IReadOnlyList<ArrlSubmissionFile> Files { get; init; }
}

/// <summary>
/// Builds the multipart body ARRL's upload form expects (issue #197).
///
/// <para><b>A pure function, on purpose.</b> The POST itself can never be exercised against ARRL
/// without filing a real session with a real VEC, so everything that can be decided without sending
/// is decided here and asserted in tests: field names, the payment-method values, the array-named
/// file part, the file count. What remains untestable is only the act of sending.</para>
///
/// <para><b>The field names are ARRL's, not ours</b> — read out of the live form's HTML. A PHP form
/// handler ignores a field it does not recognize rather than rejecting it, so a rename on either side
/// fails silently, producing a filing that looks fine and is missing a value.</para>
/// </summary>
public static class ArrlSubmissionRequest
{
    /// <summary>The file input's name, brackets included: <c>&lt;input type="file" name="the_upload[]" multiple&gt;</c>.</summary>
    public const string FilePartName = "the_upload[]";

    /// <summary>ARRL's own cap on this form is 40MB per upload; ours is the count. Two is every real case: the archive, optionally with the youth grant form.</summary>
    public const int MaxFiles = 2;

    public static MultipartFormDataContent Build(ArrlSubmissionPayload payload)
    {
        if (payload.Files.Count == 0)
        {
            throw new InvalidOperationException("An ARRL submission must include the VEC archive.");
        }

        if (payload.Files.Count > MaxFiles)
        {
            throw new InvalidOperationException(
                $"An ARRL submission carries at most {MaxFiles} files (the VEC archive and, optionally, the youth grant program form).");
        }

        var content = new MultipartFormDataContent
        {
            { Text(payload.FullName), "fullname" },
            { Text(payload.CallSign), "callsign" },
            { Text(payload.Email), "email" },
            { Text(payload.Phone), "phone" },
            { Text(payload.SessionDate), "sessionDate" },
            { Text(payload.Location), "location" },
            { Text(ToFormValue(payload.PaymentMethod)), "paymentMethod" },
            { Text(payload.AmountCharged), "amountCharged" },
            // Sent even when empty: MARC's real submissions carry an empty Notes field, so blank is a
            // legitimate value rather than a reason to omit the part.
            { Text(payload.Note ?? ""), "note" },
            // A browser sends the submit button's own name/value, and a PHP handler may branch on it.
            // Omitting it would make this request differ from a real one in a way that could only be
            // diagnosed from ARRL's side.
            { Text("Upload!"), "submit-btn" }
        };

        foreach (var file in payload.Files)
        {
            var part = new ByteArrayContent(file.Content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, FilePartName, file.FileName);
        }

        return content;
    }

    /// <summary>
    /// The radio's own values. Not <c>ToString()</c> on the enum: the C# names are readable and the
    /// wire values are ARRL's, and an unrecognized one is not rejected — it arrives as a payment
    /// method they do not recognize on a filing that otherwise looks correct.
    /// </summary>
    public static string ToFormValue(ArrlPaymentMethod method) => method switch
    {
        ArrlPaymentMethod.MailIn => "mail-in",
        ArrlPaymentMethod.PhoneIn => "phone-in",
        ArrlPaymentMethod.CreditCardOnFile => "credit-card-filed",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "No ARRL form value for this payment method.")
    };

    private static StringContent Text(string value) => new(value);
}
