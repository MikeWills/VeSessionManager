using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Posts a completed session to ARRL's upload form (issue #197). The only code in this app that
/// talks to ARRL at all.
///
/// <para><b>It returns what came back and judges nothing.</b> Deciding whether a submission landed is
/// <see cref="ArrlReceipt"/>'s job, from the response body — never from the HTTP status, since both
/// outcomes arrive on the same endpoint and this codebase has already been bitten by reading a status
/// as an answer (ExamTools' login returns 200 with an error body).</para>
///
/// <para><b>There is no retry, here or above.</b> A fire-and-forget form POST supports neither of the
/// app's usual answers to a crash between call and persistence: there is nothing to query before
/// creating, and no idempotency key ARRL would honour. A timeout after the request left the machine
/// may mean it succeeded, so absence of a receipt is not absence of a filing — that goes to a human,
/// and a silent retry would file twice.</para>
/// </summary>
public class ArrlSubmissionClient(
    HttpClient httpClient,
    IOptions<ArrlSubmissionOptions> options,
    ILogger<ArrlSubmissionClient> logger)
{
    /// <summary>
    /// Whether this deployment has an endpoint at all. Checked in the method that needs it and via
    /// this lazily-evaluated getter, never in a constructor — a constructor throw from anything
    /// resolved inside a Worker <c>BackgroundService</c> stops the entire host.
    /// </summary>
    public bool IsConfigured => options.Value.IsConfigured;

    /// <summary>
    /// Sends the submission. <b>Every call files a real session with a real VEC and cannot be
    /// undone.</b> Callers must have a human confirmation behind them and must refuse a session that
    /// already has a submission recorded.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No endpoint configured. Deliberately loud: a quiet no-op would leave somebody believing they
    /// had filed when nothing was sent.
    /// </exception>
    public async Task<ArrlSubmissionResponse> PostAsync(ArrlSubmissionPayload payload, CancellationToken cancellationToken)
    {
        if (options.Value.UploadUrl is not { } uploadUrl || string.IsNullOrWhiteSpace(uploadUrl))
        {
            throw new InvalidOperationException(
                "No ARRL upload URL is configured for this deployment, so nothing was sent. "
                + $"Set {ArrlSubmissionOptions.SectionName}:{nameof(ArrlSubmissionOptions.UploadUrl)}.");
        }

        using var content = ArrlSubmissionRequest.Build(payload);

        // Logged before the call, not after: if this throws or the process dies mid-request, the
        // submission may still have landed, and this line is the only evidence it was attempted.
        logger.LogInformation(
            "Submitting {FileCount} file(s) to ARRL for {CallSign} on {SessionDate}: {FileNames}",
            payload.Files.Count, payload.CallSign, payload.SessionDate,
            string.Join(", ", payload.Files.Select(f => f.FileName)));

        var response = await httpClient.PostAsync(uploadUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        logger.LogInformation(
            "ARRL answered {StatusCode} with {ByteCount} bytes for {CallSign} on {SessionDate}",
            (int)response.StatusCode, body.Length, payload.CallSign, payload.SessionDate);

        return new ArrlSubmissionResponse((int)response.StatusCode, body);
    }
}

/// <summary>
/// ARRL's raw answer, stored verbatim. <b>Not parsed here and not reformatted anywhere</b> — this is
/// the receipt the team keeps, and a receipt that has been tidied is worth much less if there is ever
/// a dispute about what was filed.
/// </summary>
public sealed record ArrlSubmissionResponse(int StatusCode, string Body);
