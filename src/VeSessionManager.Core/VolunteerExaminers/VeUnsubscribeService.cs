using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Letting a VE stop email from this app, from a link in the message itself (#191).
///
/// <para><b>Why it exists.</b> CAN-SPAM requires a working opt-out in commercial email, honoured
/// promptly, reachable without an account and without more than a couple of clicks. It also requires
/// the mechanism to keep working for at least 30 days after the message went out — which is why the
/// token behind it is stable rather than the single-use, short-lived kind this app uses everywhere
/// else. Somebody clicking an unsubscribe in a three-month-old email is the normal case, not an
/// edge one.</para>
///
/// <para><b>Honoured immediately and completely.</b> There is no queue and no "within ten business
/// days": the flag is set on the click, and every VE-facing sender checks it. See
/// <see cref="VolunteerExaminer.EmailUnsubscribedUtc"/> for what it covers and what it deliberately
/// does not.</para>
/// </summary>
public class VeUnsubscribeService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<AppOptions> appOptions,
    ILogger<VeUnsubscribeService> logger)
{
    /// <summary>
    /// The absolute URL to put in a message to this VE, minting the token on first use.
    ///
    /// <para>Callers must save: the mint is staged on the tracked entity rather than committed here,
    /// so that a link only becomes valid if the send it belongs to is actually recorded. Every caller
    /// is already in a unit of work that ends in <c>SaveChangesAsync</c>.</para>
    /// </summary>
    public string BuildUrl(VolunteerExaminer volunteerExaminer)
    {
        // Minted once and then reused for the life of the record. Re-minting per send would
        // invalidate the link in every message already delivered, which is precisely the failure the
        // 30-day rule exists to prevent — somebody clicking the unsubscribe in a two-month-old email
        // would land on "this link is not valid", having done exactly what they were told to do.
        volunteerExaminer.UnsubscribeToken ??= OneTimeToken.Mint();
        return $"{appOptions.Value.PublicBaseUrl.TrimEnd('/')}/ve/unsubscribe/{volunteerExaminer.UnsubscribeToken}";
    }

    /// <summary>Who a presented token belongs to, or null. Never says why it failed — this page has no business confirming whether a token exists.</summary>
    public async Task<VolunteerExaminer?> ResolveAsync(string rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var token = rawToken.Trim();
        return await dbContext.VolunteerExaminers
            .FirstOrDefaultAsync(v => v.UnsubscribeToken == token, cancellationToken);
    }

    /// <summary>Idempotent: clicking an unsubscribe twice is the same as clicking it once, and must not error.</summary>
    public async Task<bool> UnsubscribeAsync(string rawToken, CancellationToken cancellationToken)
    {
        var volunteerExaminer = await ResolveAsync(rawToken, cancellationToken);
        if (volunteerExaminer is null)
        {
            return false;
        }

        if (volunteerExaminer.EmailUnsubscribedUtc is null)
        {
            volunteerExaminer.EmailUnsubscribedUtc = timeProvider.GetUtcNow().UtcDateTime;
            // Audited with no acting user: nobody on the deployment did this, the recipient did.
            dbContext.AddAuditLog(null, "VeEmailUnsubscribed", nameof(VolunteerExaminer), volunteerExaminer.Id,
                "VE unsubscribed from email using the link in a message.", volunteerExaminer.EmailUnsubscribedUtc.Value);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("VE {VolunteerExaminerId} unsubscribed from email", volunteerExaminer.Id);
        }

        return true;
    }

    /// <summary>
    /// The other direction, from the same page — somebody who unsubscribed by accident should not
    /// have to telephone a team admin to undo it.
    /// </summary>
    public async Task<bool> ResubscribeAsync(string rawToken, CancellationToken cancellationToken)
    {
        var volunteerExaminer = await ResolveAsync(rawToken, cancellationToken);
        if (volunteerExaminer is null)
        {
            return false;
        }

        if (volunteerExaminer.EmailUnsubscribedUtc is not null)
        {
            volunteerExaminer.EmailUnsubscribedUtc = null;
            dbContext.AddAuditLog(null, "VeEmailResubscribed", nameof(VolunteerExaminer), volunteerExaminer.Id,
                "VE resumed email using the link in a message.", timeProvider.GetUtcNow().UtcDateTime);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("VE {VolunteerExaminerId} resumed email", volunteerExaminer.Id);
        }

        return true;
    }
}
