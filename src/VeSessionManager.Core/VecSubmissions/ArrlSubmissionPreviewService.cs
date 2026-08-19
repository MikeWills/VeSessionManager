using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;
using VeSessionManager.Core.ExamTools;
using VeSessionManager.Core.Uls;

namespace VeSessionManager.Core.VecSubmissions;

/// <summary>
/// Builds the ARRL submission preview (issue #197): every form field with the value that would be
/// posted, the archive that would go with it, and the review aids a human needs to judge it.
///
/// <para><b>Resolves and reports; it never sends.</b> The POST lives in its own service — this one
/// exists so the screen can show exactly what would be filed, which is the only safeguard available
/// for a code path that has no sandbox and cannot be tested end to end.</para>
/// </summary>
public class ArrlSubmissionPreviewService(
    AppDbContext dbContext,
    IExamToolsClient examToolsClient,
    IOptions<ExamToolsOptions> examToolsOptions,
    ILogger<ArrlSubmissionPreviewService> logger)
{
    /// <summary>
    /// <c>Vec.MatchCode</c> for ARRL, lower-cased. Matched on the code and <b>never the display
    /// name</b>, which is "ARRL" on this deployment and "ARRL-VEC" upstream.
    /// </summary>
    public const string ArrlMatchCode = "arrl";

    public async Task<ArrlSubmissionPreview> BuildAsync(int sessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .Include(s => s.Team)
            .Include(s => s.Vec)
            .Include(s => s.FeeConfiguration)
            .Include(s => s.Candidates).ThenInclude(c => c.Payments).ThenInclude(p => p.Refunds)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return new ArrlSubmissionPreview { Status = ArrlSubmissionPreviewStatus.SessionNotFound, SessionId = sessionId };
        }

        var team = session.Team;
        var basics = new ArrlSubmissionPreview
        {
            Status = ArrlSubmissionPreviewStatus.Ready,
            SessionId = session.Id,
            SessionTitle = session.Title,
            TeamName = team.Name,
            AlreadySubmitted = session.VecSubmissionStatus == VecSubmissionStatus.Submitted
        };

        // One submitter, no fallback (#197's first constraint): a session under any other VEC must
        // find nothing rather than be handed ARRL's submitter.
        if (!string.Equals(session.Vec.MatchCode, ArrlMatchCode, StringComparison.OrdinalIgnoreCase))
        {
            return basics with { Status = ArrlSubmissionPreviewStatus.NotAnArrlSession };
        }

        // Checked before the archive is fetched, so an unconfigured team costs nothing and is told
        // what is wrong rather than watching a download succeed into a form it cannot fill.
        if (!team.IsArrlSubmissionConfigured)
        {
            return basics with { Status = ArrlSubmissionPreviewStatus.TeamNotConfigured };
        }

        var lead = await ResolveLeadAsync(session, cancellationToken);
        var fees = session.GetFeeSummary();

        var email = team.ArrlSubmissionEmailSource == ArrlSubmissionEmailSource.TeamAddress
            ? team.ArrlSubmissionEmail
            : lead?.Email;

        // The lead's name plus the team's postfix, concatenated verbatim — HRCC's real value opens
        // with a slash and no space, and inserting a separator would change what is filed.
        var fullName = lead?.Name is { } leadName
            ? leadName + (team.ArrlSubmissionNamePostfix ?? "")
            : null;

        var preview = basics with
        {
            FullName = NullIfBlank(fullName),
            CallSign = NullIfBlank(lead?.CallSign),
            Email = NullIfBlank(email),
            Phone = NullIfBlank(lead?.Phone),
            // Eastern, not UTC. 697 of 867 stored sessions start between 23:00 and 04:00 UTC, so
            // .Date would file tomorrow's date for most of them — the #248 bug class.
            SessionDate = UlsSchedule.ToEasternDate(session.ScheduledStartUtc)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Location = team.ArrlSubmissionLocation,
            PaymentMethod = team.ArrlSubmissionPaymentMethod,
            AmountCharged = Usd.Raw(fees.TotalRemitToVec),
            Note = team.ArrlSubmissionNote,
            Fees = fees,
            AmountWarnings = BuildAmountWarnings(session),
            YouthFormExpected = HasYouthRatePayment(session)
        };

        preview = preview with { MissingRequiredFields = FindMissingFields(preview) };

        return await AttachArchiveAsync(preview, session, team, cancellationToken);
    }

    /// <summary>
    /// The same resolution <c>MessageDispatchService</c> does for a rule's Reply-To — normalized call
    /// sign to a <see cref="VolunteerExaminer"/>. Null when the session names no lead, or names one
    /// with no matching record.
    /// </summary>
    private async Task<VolunteerExaminer?> ResolveLeadAsync(Session session, CancellationToken cancellationToken)
    {
        if (CallSign.Normalize(session.TeamLeadCallSign) is not { } callSign)
        {
            return null;
        }

        var lead = await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.CallSign == callSign, cancellationToken);
        if (lead is null)
        {
            logger.LogInformation(
                "Session {SessionId} names lead {CallSign}, who has no VE record — the ARRL form's contact fields cannot be prefilled",
                session.Id, callSign);
        }

        return lead;
    }

    /// <summary>
    /// ARRL marks all four contact fields required, and none of them is guaranteed: ExamTools supplies
    /// no contact details at all, so email and phone are only ever filled in by an admin or the VE —
    /// and the retention purge clears both. Named individually so the operator knows which box to fill.
    /// </summary>
    private static List<string> FindMissingFields(ArrlSubmissionPreview preview)
    {
        var missing = new List<string>();
        if (preview.FullName is null) missing.Add("Full name");
        if (preview.CallSign is null) missing.Add("Call sign");
        if (preview.Email is null) missing.Add("Email address");
        if (preview.Phone is null) missing.Add("Phone number");
        return missing;
    }

    /// <summary>
    /// The two ways <c>GetFeeSummary</c>'s total can disagree with money actually received. Neither is
    /// corrected automatically — only a human knows whether a refunded candidate was filed, or whether
    /// a short payment is being chased — but a confident number with no sign that its inputs are
    /// unusual is worse than no derivation at all.
    /// </summary>
    private static List<string> BuildAmountWarnings(Session session)
    {
        var warnings = new List<string>();
        var paid = session.Candidates.SelectMany(c => c.Payments).Where(p => p.Status == PaymentStatus.Paid).ToList();

        // A refund deliberately does not move a payment off Paid (#375) — otherwise the "unpaid and
        // no link" scan would issue the candidate a fresh checkout link — so the total still counts it.
        var refunded = paid.Count(p => p.Refunds.Count > 0);
        if (refunded > 0)
        {
            warnings.Add($"{refunded} payment(s) feeding this total have a refund against them, which does not reduce the amount above.");
        }

        // Square reported a different figure than was owed — the out-of-band youth rate is the routine
        // cause, and it leaves Amount at the standard rate while less money arrived.
        var mismatched = paid.Count(p => p.AmountMismatchFlaggedUtc is not null);
        if (mismatched > 0)
        {
            warnings.Add($"{mismatched} payment(s) were flagged because the amount Square reported differs from the amount owed.");
        }

        return warnings;
    }

    /// <summary>A youth-rate payment is when ARRL also expects the youth grant program form — the second of the two files.</summary>
    private static bool HasYouthRatePayment(Session session)
    {
        if (session.FeeConfiguration.YouthExamFeeAmount is not { } youthAmount)
        {
            return false;
        }

        return session.Candidates
            .SelectMany(c => c.Payments)
            .Where(p => p.Status == PaymentStatus.Paid)
            .Any(p => p.Amount == youthAmount || p.SquareAmountPaidUsd == youthAmount);
    }

    /// <summary>
    /// Fetches the archive again at submission time, for the bytes this time rather than a
    /// description of them.
    ///
    /// <para><b>Re-fetched rather than carried over from the preview.</b> A page render and a confirm
    /// are two requests, and holding several hundred kilobytes across them to save one call would
    /// trade a real cost for a false economy — and would file whatever the archive looked like when
    /// the page was opened rather than when the button was pressed.</para>
    /// </summary>
    public async Task<ArrlSubmissionFile?> FetchArchiveFileAsync(int sessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .Include(s => s.Team)
            .Include(s => s.Vec)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session is null || !session.Team.IsExamToolsConfigured)
        {
            return null;
        }

        var credentials = ExamToolsCredentials.For(session.Team, examToolsOptions.Value.BaseUrl);
        var download = await examToolsClient.DownloadVecArchiveAsync(
            credentials, session.ExamToolsSessionId, session.Vec.MatchCode, cancellationToken);

        if (download.Outcome != VecArchiveDownloadOutcome.Succeeded || download.Content is null)
        {
            return null;
        }

        var fileName = download.FileName
                       ?? VecArchiveFileName.Build(session.Team.ExamToolsTeamCode!, session.ScheduledStartUtc, session.Vec.MatchCode);

        return new ArrlSubmissionFile(fileName, download.Content);
    }

    private async Task<ArrlSubmissionPreview> AttachArchiveAsync(
        ArrlSubmissionPreview preview, Session session, Team team, CancellationToken cancellationToken)
    {
        if (!team.IsExamToolsConfigured)
        {
            return preview with
            {
                ArchiveMessage = "This team has no ExamTools credentials, so the VEC archive cannot be downloaded."
            };
        }

        var credentials = ExamToolsCredentials.For(team, examToolsOptions.Value.BaseUrl);
        var download = await examToolsClient.DownloadVecArchiveAsync(
            credentials, session.ExamToolsSessionId, session.Vec.MatchCode, cancellationToken);

        if (download.Outcome != VecArchiveDownloadOutcome.Succeeded)
        {
            return preview with { ArchiveOutcome = download.Outcome, ArchiveMessage = download.Message };
        }

        // Content-Disposition normally supplies this. When it does not, rebuild the descriptive name
        // rather than falling back to the URL's, which is identical for every session of every team.
        var fileName = download.FileName
                       ?? VecArchiveFileName.Build(team.ExamToolsTeamCode!, session.ScheduledStartUtc, session.Vec.MatchCode);

        return preview with
        {
            ArchiveOutcome = download.Outcome,
            ArchiveFileName = fileName,
            ArchiveByteCount = download.Content?.Length ?? 0
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
