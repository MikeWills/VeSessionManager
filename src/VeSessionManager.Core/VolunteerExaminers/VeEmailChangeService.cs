using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Lets a VE change their own email address, confirmed from the address they already hold
/// (issue #142 phase 5, decided with Mike 2026-08-07).
///
/// <para><b>Why the confirmation goes to the OLD address.</b> This field is not just contact detail —
/// it is the credential for self-service sign-in. Applying a change on the strength of the session
/// that requested it would make one leaked link permanent takeover: whoever holds it points the
/// address at themselves and every future link follows. Requiring the current mailbox to approve
/// caps a stolen link at a single session.</para>
///
/// <para><b>And the confirmation names the new address.</b> Approval from the old mailbox authorises
/// the change; showing what it will become is what catches a typo, which would otherwise send every
/// future link somewhere unreadable and leave an admin as the only way back.</para>
/// </summary>
public class VeEmailChangeService(
    AppDbContext dbContext,
    SystemSettingsService systemSettingsService,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<VeEmailChangeService> logger)
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    /// <summary>Longer than the sign-in link's thirty minutes: the person who has to act is reading their *old* mailbox, which they may not be watching.</summary>
    public static readonly TimeSpan RequestThrottle = TimeSpan.FromMinutes(5);

    public async Task<VeEmailChangeResult> RequestAsync(
        int volunteerExaminerId, string newEmail, Func<string, string> linkFactory, CancellationToken cancellationToken)
    {
        var person = await dbContext.VolunteerExaminers.FirstOrDefaultAsync(v => v.Id == volunteerExaminerId, cancellationToken);
        if (person is null)
        {
            return VeEmailChangeResult.NotFound;
        }

        newEmail = (newEmail ?? "").Trim();
        if (newEmail.Length == 0 || !newEmail.Contains('@'))
        {
            return VeEmailChangeResult.InvalidEmail;
        }

        if (string.IsNullOrWhiteSpace(person.Email))
        {
            // Nothing to confirm against. Unreachable through self-service — signing in requires an
            // address — but a caller could still get here, and silently applying the change would be
            // exactly the unconfirmed path this service exists to prevent.
            return VeEmailChangeResult.NoCurrentEmail;
        }

        if (string.Equals(person.Email, newEmail, StringComparison.OrdinalIgnoreCase))
        {
            return VeEmailChangeResult.Unchanged;
        }

        // Two VEs sharing an address would make sign-in ambiguous — the lookup takes the first match,
        // so one of them would silently receive the other's links. Rejected outright rather than
        // resolved by guessing.
        var takenByAnother = await dbContext.VolunteerExaminers
            .AnyAsync(v => v.Id != person.Id && v.Email != null && v.Email.ToLower() == newEmail.ToLower(), cancellationToken);
        if (takenByAnother)
        {
            return VeEmailChangeResult.AlreadyInUse;
        }

        var settings = await systemSettingsService.GetAsync(cancellationToken);
        if (!settings.IsSystemEmailConfigured)
        {
            logger.LogWarning("VE email change requested but no system SMTP sender is configured");
            return VeEmailChangeResult.SystemEmailNotConfigured;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var recent = await dbContext.VeEmailChangeRequests
            .AnyAsync(r => r.VolunteerExaminerId == person.Id && r.ConfirmedUtc == null && r.CreatedUtc > now - RequestThrottle, cancellationToken);
        if (recent)
        {
            return VeEmailChangeResult.Throttled;
        }

        // Any earlier pending request is abandoned. Two live links changing the address to different
        // values, whichever is clicked last winning, is not a race worth having.
        // RemoveRange rather than ExecuteDelete: at most a couple of rows per VE, and ExecuteDelete
        // is unsupported on EF InMemory, which would make this path untestable outside SQLite for no
        // gain at this size.
        var superseded = await dbContext.VeEmailChangeRequests
            .Where(r => r.VolunteerExaminerId == person.Id && r.ConfirmedUtc == null)
            .ToListAsync(cancellationToken);
        if (superseded.Count > 0)
        {
            dbContext.VeEmailChangeRequests.RemoveRange(superseded);
            logger.LogInformation("Superseded {Count} pending email change(s) for VE {VolunteerExaminerId}", superseded.Count, person.Id);
        }

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        dbContext.VeEmailChangeRequests.Add(new VeEmailChangeRequest
        {
            VolunteerExaminerId = person.Id,
            NewEmail = newEmail,
            ConfirmationSentToEmail = person.Email!,
            TokenHash = Hash(rawToken),
            CreatedUtc = now,
            ExpiresUtc = now + TokenLifetime
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        var link = linkFactory(rawToken);
        var body = $"""
                    <p>Hello {System.Net.WebUtility.HtmlEncode(person.Name)},</p>
                    <p>Someone asked to change the email address your VE team holds for you to
                    <strong>{System.Net.WebUtility.HtmlEncode(newEmail)}</strong>.</p>
                    <p>If that is right, confirm it here — the link expires in
                    {TokenLifetime.TotalHours:F0} hours:</p>
                    <p><a href="{link}">Confirm my new email address</a></p>
                    <p><strong>If you did not ask for this, do not click the link.</strong> Nothing has
                    changed yet, and your details are still reachable from this address. Tell your team
                    admin if you were not expecting it.</p>
                    """;

        await emailSender.SendAsync(
            settings.ToSystemEmailCredentials(),
            new EmailMessage(
                ToAddress: person.Email!,
                FromAddress: settings.SystemSmtpFromAddress ?? settings.SystemSmtpUsername!,
                FromDisplayName: settings.SystemSmtpFromDisplayName ?? "VE Session Manager",
                ReplyToAddress: settings.SystemSmtpFromAddress ?? settings.SystemSmtpUsername!,
                Subject: "Confirm your new email address",
                HtmlBody: body),
            cancellationToken);

        logger.LogInformation("Email change requested for VE {VolunteerExaminerId}; confirmation sent to the current address", person.Id);
        return VeEmailChangeResult.ConfirmationSent;
    }

    /// <summary>Applies a change whose confirmation link has been followed from the old address.</summary>
    /// <summary>
    /// Reports what <see cref="ConfirmAsync"/> would do, changing nothing (#290).
    ///
    /// <para>Exists because applying the change on a GET meant that link-prefetching mail security
    /// gateways, corporate URL scanners and browser prefetch could confirm an address change the VE
    /// never decided to make — all of those routinely fetch links in email. The page now renders a
    /// button on GET and confirms on POST, and this is what lets the GET say something truthful about
    /// a link it has not yet used.</para>
    ///
    /// <para><b>Deliberately not applied to the sign-in link at <c>Enter</c>.</b> That one consumes
    /// its single-use token on GET on purpose: a sign-in link that survives being followed is a
    /// credential sitting in an inbox. The accepted cost there is that a scanner burns the link and
    /// the VE sees "no longer valid" — a different trade, made knowingly, and not to be "fixed" to
    /// match this.</para>
    /// </summary>
    public async Task<(VeEmailChangeResult Result, string? NewEmail)> PeekAsync(string rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return (VeEmailChangeResult.NotFound, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var request = await dbContext.VeEmailChangeRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TokenHash == Hash(rawToken.Trim()), cancellationToken);

        if (request is null || request.ConfirmedUtc is not null || request.ExpiresUtc <= now)
        {
            return (VeEmailChangeResult.NotFound, null);
        }

        // The taken-by-another check is deliberately NOT repeated here. It is re-evaluated by
        // ConfirmAsync at the moment of the write, which is the only evaluation that can be relied
        // on; duplicating it would just add a second answer that can be stale by the time it matters.
        return (VeEmailChangeResult.Confirmed, request.NewEmail);
    }

    public async Task<(VeEmailChangeResult Result, string? NewEmail)> ConfirmAsync(string rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return (VeEmailChangeResult.NotFound, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var request = await dbContext.VeEmailChangeRequests
            .Include(r => r.VolunteerExaminer)
            .FirstOrDefaultAsync(r => r.TokenHash == Hash(rawToken.Trim()), cancellationToken);

        if (request is null || request.ConfirmedUtc is not null || request.ExpiresUtc <= now)
        {
            return (VeEmailChangeResult.NotFound, null);
        }

        // Re-checked at confirmation, not just at request time: someone else may have taken the
        // address during the 24 hours the link was valid, and applying it anyway would make two VEs
        // share a sign-in address.
        var takenByAnother = await dbContext.VolunteerExaminers
            .AnyAsync(v => v.Id != request.VolunteerExaminerId && v.Email != null && v.Email.ToLower() == request.NewEmail.ToLower(), cancellationToken);
        if (takenByAnother)
        {
            return (VeEmailChangeResult.AlreadyInUse, null);
        }

        var previous = request.VolunteerExaminer.Email;
        request.VolunteerExaminer.Email = request.NewEmail;
        request.VolunteerExaminer.UpdatedUtc = now;
        request.ConfirmedUtc = now;

        // Attributed to no user: the VE did this themselves, and inventing an acting admin would make
        // the audit trail say something untrue. The details line carries who it actually was.
        dbContext.AddAuditLog(null, "VeEmailChangedBySelf", nameof(VolunteerExaminer), request.VolunteerExaminerId,
            $"VE {request.VolunteerExaminer.CallSign ?? request.VolunteerExaminer.Name} changed their own email " +
            $"from {previous} to {request.NewEmail}, confirmed from {request.ConfirmationSentToEmail}.",
            now);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("VE {VolunteerExaminerId} confirmed an email change", request.VolunteerExaminerId);
        return (VeEmailChangeResult.Confirmed, request.NewEmail);
    }

    private static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
}

public enum VeEmailChangeResult
{
    ConfirmationSent,
    Confirmed,
    NotFound,
    InvalidEmail,
    Unchanged,

    /// <summary>Another VE already uses that address. Sign-in resolves an address to one person, so two cannot share one.</summary>
    AlreadyInUse,

    /// <summary>No current address to confirm against — unreachable via self-service, which requires one to sign in.</summary>
    NoCurrentEmail,

    Throttled,
    SystemEmailNotConfigured
}
