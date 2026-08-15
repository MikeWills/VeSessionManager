using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.VolunteerExaminers;

/// <summary>
/// Issues and redeems the sign-in links that let a VE maintain their own contact details without an
/// account (issue #142) — "lazy login to their email".
///
/// <para><b>This is the app's first unauthenticated endpoint that reaches personal data</b>, which
/// is why it was scheduled last. Every decision below is about that: no account enumeration, tokens
/// hashed at rest, single use, short life, throttled per address, and a session that expires on its
/// own rather than lingering.</para>
///
/// <para>Modelled closely on <c>PasswordResetService</c>, including sending from the deployment-wide
/// SMTP sender rather than a team's: a VE may serve several teams, and picking one of them to send
/// as would be arbitrary.</para>
/// </summary>
public class VeSelfServiceLinkService(
    AppDbContext dbContext,
    SystemSettingsService systemSettingsService,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<VeSelfServiceLinkService> logger)
{
    /// <summary>
    /// How long a link works for. Shorter than a password reset's, because this one needs no second
    /// factor at all — possession of the email is the whole proof — and an emailed link outlives the
    /// moment it was wanted.
    /// </summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long the signed-in session lasts once a link is redeemed. Long enough to type an address, short enough that a shared machine does not stay open.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Minimum gap between links to one address. The threat is using this endpoint to bombard a
    /// mailbox or burn SMTP quota; one link every few minutes covers any genuinely confused user.
    /// </summary>
    public static readonly TimeSpan RequestThrottle = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Always reports success for any syntactically usable address, whether or not a VE has it.
    ///
    /// <para><b>Anything else turns this into a membership oracle</b> — "no such VE" would let anyone
    /// enumerate which addresses belong to volunteer examiners on this deployment, which is exactly
    /// the sort of thing a roster of people's home details should not leak. The single exception is a
    /// missing SMTP sender, which is a deployment fault the admin must see rather than a fact about
    /// any person.</para>
    /// </summary>
    public async Task<VeSelfServiceRequestResult> RequestLinkAsync(
        string email, Func<string, string> linkFactory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return VeSelfServiceRequestResult.Accepted;
        }

        var settings = await systemSettingsService.GetAsync(cancellationToken);
        if (!settings.IsSystemEmailConfigured)
        {
            logger.LogWarning("VE self-service link requested but no system SMTP sender is configured — set SystemSettings.SystemSmtp* (Admin -> System Settings)");
            return VeSelfServiceRequestResult.SystemEmailNotConfigured;
        }

        var normalized = email.Trim();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // OrderBy before First, on a predicate that is not guaranteed unique by the database (#284).
        // Four code paths enforce "one VE per email" and none was backed by an index, so two rows
        // could hold the same address — and this call mails a sign-in link, a bearer credential
        // reaching that person's own contact details. Without an order, which of the two received it
        // was whatever SQLite happened to return first.
        //
        // The unique index added alongside this makes the ambiguity unreachable going forward. The
        // ordering stays regardless: it costs nothing, and "the index guarantees one row" is exactly
        // the kind of assumption that outlives the index.
        var volunteerExaminer = await dbContext.VolunteerExaminers
            .Where(v => v.Email != null && v.Email.ToLower() == normalized.ToLower())
            .OrderBy(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (volunteerExaminer is null)
        {
            // Deliberately silent, and deliberately the same outcome as success. Logged at
            // information rather than warning: an unknown address here is a typo far more often than
            // it is an attack, and a warning-level line per typo trains people to ignore warnings.
            logger.LogInformation("VE self-service link requested for an address with no matching VE — reporting success anyway");
            return VeSelfServiceRequestResult.Accepted;
        }

        var recentlyIssued = await dbContext.VeSelfServiceTokens
            .AnyAsync(t => t.VolunteerExaminerId == volunteerExaminer.Id && t.CreatedUtc > now - RequestThrottle, cancellationToken);
        if (recentlyIssued)
        {
            // Same outward answer again: telling the caller they are being throttled confirms the
            // address exists, which is precisely what the silence above is protecting.
            logger.LogInformation("VE self-service link throttled for VE {VolunteerExaminerId}", volunteerExaminer.Id);
            return VeSelfServiceRequestResult.Accepted;
        }

        // 32 bytes from a CSPRNG, URL-safe. Only the hash is stored — see VeSelfServiceToken.
        var rawToken = OneTimeToken.Mint();

        dbContext.VeSelfServiceTokens.Add(new VeSelfServiceToken
        {
            VolunteerExaminerId = volunteerExaminer.Id,
            TokenHash = OneTimeToken.Hash(rawToken),
            CreatedUtc = now,
            ExpiresUtc = now + TokenLifetime,
            SentToEmail = volunteerExaminer.Email!
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        var link = linkFactory(rawToken);
        var body = $"""
                    <p>Hello {System.Net.WebUtility.HtmlEncode(volunteerExaminer.Name)},</p>
                    <p>Use the link below to review and update the contact details your VE team holds
                    for you. It works once and expires in {TokenLifetime.TotalMinutes:F0} minutes.</p>
                    <p><a href="{link}">Update my details</a></p>
                    <p>If you did not ask for this, you can ignore this email — nothing has changed.</p>
                    """;

        // The deployment-wide sender, not a team's: a VE may serve several teams, so sending as one
        // of them would be arbitrary. Same reasoning as password reset, which shares this sender.
        await emailSender.SendAsync(
            settings.ToSystemEmailCredentials(),
            new EmailMessage(
                ToAddress: volunteerExaminer.Email!,
                FromAddress: settings.SystemSmtpFromAddress ?? settings.SystemSmtpUsername!,
                FromDisplayName: settings.SystemSmtpFromDisplayName ?? "VE Session Manager",
                ReplyToAddress: settings.SystemSmtpFromAddress ?? settings.SystemSmtpUsername!,
                Subject: "Update your volunteer examiner details",
                HtmlBody: body),
            cancellationToken);

        logger.LogInformation("Issued a VE self-service link to VE {VolunteerExaminerId}", volunteerExaminer.Id);
        return VeSelfServiceRequestResult.Accepted;
    }

    /// <summary>
    /// Validates a presented token and consumes it. Returns the VE it belongs to, or null for
    /// anything unusable — expired, already used, or never issued. The caller must not distinguish
    /// those to the visitor.
    /// </summary>
    public async Task<VolunteerExaminer?> RedeemAsync(string rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var hash = OneTimeToken.Hash(rawToken.Trim());

        var token = await dbContext.VeSelfServiceTokens
            .Include(t => t.VolunteerExaminer)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (token is null || token.ConsumedUtc is not null || token.ExpiresUtc <= now)
        {
            return null;
        }

        // Consumed at first use, not at end of session: the link is the credential, and a link that
        // still works after it has been followed is a link sitting in an inbox waiting to be found.
        token.ConsumedUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        return token.VolunteerExaminer;
    }

    /// <summary>
    /// Removes tokens that can no longer be used. Not security-critical — a consumed or expired token
    /// is already inert — but the table would otherwise grow forever.
    /// <para>Uses ExecuteDelete, which needs a relational provider: this is a bulk maintenance sweep
    /// where loading every row to delete it would be the wrong shape, unlike the small per-VE delete
    /// in VeEmailChangeService.</para>
    /// </summary>
    public async Task<int> PurgeSpentTokensAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(7);
        return await dbContext.VeSelfServiceTokens
            .Where(t => t.ExpiresUtc < cutoff || (t.ConsumedUtc != null && t.ConsumedUtc < cutoff))
            .ExecuteDeleteAsync(cancellationToken);
    }

}

public enum VeSelfServiceRequestResult
{
    /// <summary>Reported for every usable address, whether or not a VE has it — see RequestLinkAsync.</summary>
    Accepted,

    /// <summary>A deployment misconfiguration the admin must see. Not a fact about any person.</summary>
    SystemEmailNotConfigured
}
