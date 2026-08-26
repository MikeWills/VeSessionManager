using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Files a session with ARRL-VEC (issue #197). The one place in this app that causes a submission.
///
/// <para><b>Every call files a real session with a real VEC and cannot be recalled.</b> There is no
/// rollback — the answer is "contact ARRL" — so the guards here are not defensive habit, they are the
/// feature. Nothing calls this except a human pressing confirm on a screen showing exactly what would
/// be sent.</para>
///
/// <para><b>There is no retry, anywhere.</b> A fire-and-forget form POST supports neither
/// query-before-create nor a persisted idempotency key, ARRL cannot dedupe, and a timeout after the
/// request left the machine may mean it succeeded. Absence of a receipt is not absence of a filing,
/// so an ambiguous outcome goes to a human.</para>
/// </summary>
public class ArrlSubmissionService(
    AppDbContext dbContext,
    ArrlSubmissionClient client,
    ArrlSubmissionArchiveStore archiveStore,
    IOptions<ExamToolsOptions> examToolsOptions,
    TimeProvider timeProvider,
    ILogger<ArrlSubmissionService> logger)
{
    /// <summary>
    /// Sends one submission and records it.
    /// </summary>
    /// <param name="attachment">The youth grant program form, or null. ARRL's form takes at most two files.</param>
    public async Task<ArrlSubmitResult> SubmitAsync(
        int sessionId,
        ArrlSubmissionFieldValues fields,
        ArrlSubmissionFile archive,
        ArrlSubmissionFile? attachment,
        int userId,
        CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .Include(s => s.Team)
            .Include(s => s.Vec)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return ArrlSubmitResult.SessionNotFound;
        }

        // Two guards against a second filing, because there are two ways to arrive here: the session's
        // own one-way toggle, and a submission row from an attempt whose outcome we could not read.
        // The second matters more — an Unknown attempt may already have landed, and re-sending it is
        // exactly the duplicate this cannot undo.
        if (session.VecSubmissionStatus == VecSubmissionStatus.Submitted)
        {
            return ArrlSubmitResult.AlreadySubmitted;
        }

        if (await dbContext.ArrlVecSubmissions.AnyAsync(s => s.SessionId == sessionId, cancellationToken))
        {
            return ArrlSubmitResult.AlreadyAttempted;
        }

        if (!client.IsConfigured)
        {
            return ArrlSubmitResult.NotConfigured;
        }

        // Defense in depth against ArrlSubmissionPreviewService's own gate: this is the one place a
        // submission actually happens, and the preview screen is not the only way a caller could reach
        // it. A team practicing against ExamTools' test site must never be able to file a real session
        // with a real VEC, regardless of what led here.
        if (ExamToolsCredentials.For(session.Team, examToolsOptions.Value.BaseUrl).IsTestEnvironment)
        {
            return ArrlSubmitResult.TeamOnTestExamTools;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var files = attachment is null ? new[] { archive } : [archive, attachment];

        var submission = new ArrlVecSubmission
        {
            SessionId = session.Id,
            TeamId = session.TeamId,
            SubmittedByUserId = userId,
            SubmittedUtc = now,
            FullName = fields.FullName,
            CallSign = fields.CallSign,
            Email = fields.Email,
            Phone = fields.Phone,
            SessionDate = fields.SessionDate,
            Location = fields.Location,
            PaymentMethod = fields.PaymentMethod,
            AmountCharged = fields.AmountCharged,
            Note = fields.Note,
            ArchiveFileName = archive.FileName,
            ArchiveByteCount = archive.Content.Length,
            AttachmentFileName = attachment?.FileName,
            AttachmentByteCount = attachment?.Content.Length ?? 0,
            Outcome = ArrlReceiptOutcome.Unknown
        };

        // Evidence first. If the POST throws, or the process dies mid-request, the submission may
        // still have landed — and then this is the only record of what was sent. Storing afterwards
        // would lose exactly the case the archive exists for.
        await StoreFilesAsync(submission, session, files, cancellationToken);

        var payload = new ArrlSubmissionPayload
        {
            FullName = fields.FullName,
            CallSign = fields.CallSign,
            Email = fields.Email,
            Phone = fields.Phone,
            SessionDate = fields.SessionDate,
            Location = fields.Location,
            PaymentMethod = fields.PaymentMethod,
            AmountCharged = fields.AmountCharged,
            Note = fields.Note,
            Files = files
        };

        try
        {
            var response = await client.PostAsync(payload, cancellationToken);
            var receipt = ArrlReceipt.Read(response.Body, [.. files.Select(f => f.FileName)]);

            submission.ResponseStatusCode = response.StatusCode;
            submission.ResponseBody = response.Body;
            submission.Outcome = receipt.Outcome;
            submission.UnconfirmedFileNames = receipt.UnconfirmedFileNames.Count == 0
                ? null
                : string.Join(", ", receipt.UnconfirmedFileNames);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Deliberately not rethrown, and deliberately not a retry. The request left this machine;
            // ARRL may have processed it. Recording the attempt is what lets a human resolve it.
            logger.LogError(ex, "The ARRL submission for session {SessionId} did not complete. It may still have been filed.", sessionId);
            submission.TransportError = ex.Message;
            submission.Outcome = ArrlReceiptOutcome.Unknown;
        }

        dbContext.ArrlVecSubmissions.Add(submission);

        // Only a positively confirmed receipt marks the session filed. An Unknown outcome leaves the
        // session unsubmitted on purpose — it is the state that needs a human, and marking it would
        // hide exactly the thing that needs looking at.
        if (submission.Outcome == ArrlReceiptOutcome.Succeeded)
        {
            session.VecSubmissionStatus = VecSubmissionStatus.Submitted;
            session.VecSubmittedDate = now;
            session.VecSubmittedByUserId = userId;
        }

        dbContext.AddAuditLog(userId,
            submission.Outcome == ArrlReceiptOutcome.Succeeded ? "ArrlSubmissionFiled" : "ArrlSubmissionUnconfirmed",
            nameof(Session), session.Id,
            submission.Outcome == ArrlReceiptOutcome.Succeeded
                ? $"Filed {files.Length} file(s) with ARRL-VEC; receipt confirms {submission.ArchiveFileName}."
                : $"Sent {files.Length} file(s) to ARRL-VEC; no confirmation could be read. This may still have been filed — check with ARRL before resending.",
            now);

        await dbContext.SaveChangesAsync(cancellationToken);

        return submission.Outcome == ArrlReceiptOutcome.Succeeded
            ? ArrlSubmitResult.Succeeded
            : ArrlSubmitResult.Unconfirmed;
    }

    /// <summary>
    /// Writes the evidence under <c>team/vec/year/month</c>. A storage failure does not stop the
    /// submission: losing the archive is bad, and not filing a session because a disk was full would
    /// be worse — but it is logged loudly, and the row records that a path is missing.
    /// </summary>
    private async Task StoreFilesAsync(
        ArrlVecSubmission submission, Session session, IReadOnlyList<ArrlSubmissionFile> files, CancellationToken cancellationToken)
    {
        if (!archiveStore.IsConfigured)
        {
            logger.LogWarning(
                "No ARRL archive directory is configured, so nothing filed for session {SessionId} is being kept.", session.Id);
            return;
        }

        var directory = ArrlSubmissionArchiveStore.BuildRelativeDirectory(
            session.Team.ExamToolsTeamCode ?? session.Team.Name, session.Vec.MatchCode, session.ScheduledStartUtc);

        try
        {
            submission.ArchiveStoredPath = await archiveStore.SaveAsync(directory, files[0].FileName, files[0].Content, cancellationToken);
            if (files.Count > 1)
            {
                submission.AttachmentStoredPath = await archiveStore.SaveAsync(directory, files[1].FileName, files[1].Content, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not keep the ARRL submission evidence for session {SessionId}; the submission itself is going ahead.", session.Id);
        }
    }
}

/// <summary>The form's editable values, as they stood on screen when confirm was pressed.</summary>
public sealed record ArrlSubmissionFieldValues
{
    public required string FullName { get; init; }
    public required string CallSign { get; init; }
    public required string Email { get; init; }
    public required string Phone { get; init; }
    public required string SessionDate { get; init; }
    public required string Location { get; init; }
    public required ArrlPaymentMethod PaymentMethod { get; init; }
    public required string AmountCharged { get; init; }
    public string? Note { get; init; }
}

public enum ArrlSubmitResult
{
    /// <summary>ARRL's receipt confirmed every file. The only result that marks the session submitted.</summary>
    Succeeded,

    /// <summary>
    /// It was sent and no confirmation could be read. <b>Not a failure</b> — it may well have been
    /// filed. Needs a human to check with ARRL, and must never be resent automatically.
    /// </summary>
    Unconfirmed,

    SessionNotFound,
    AlreadySubmitted,

    /// <summary>A submission row already exists for this session, including an unconfirmed one. Resending is the duplicate nobody can undo.</summary>
    AlreadyAttempted,

    /// <summary>No upload URL on this deployment. Loud rather than quiet: a silent no-op would leave somebody believing they had filed.</summary>
    NotConfigured,

    /// <summary>This team's effective ExamTools host is the test site, not production. A team practicing against test data must never file a real session with ARRL.</summary>
    TeamOnTestExamTools
}
