using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VeSessionManager.Core.Admin;
using VeSessionManager.Core.Data;
using VeSessionManager.Core.Email;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Authorization;

/// <summary>
/// Self-service "forgot password" for local (email + password) accounts. OAuth users never reach
/// this — Google/Microsoft own their credentials.
///
/// Built 2026-08-01: before it, a user who forgot their password was locked out permanently and the
/// only recovery was editing AspNetUsers by hand. Identity's token machinery was already available
/// (Program.cs registers AddDefaultTokenProviders), so this is plumbing, not new infrastructure.
///
/// Mail sends from the deployment-wide SystemSettings.SystemSmtp* sender, NOT a team's — a reset is
/// addressed to an app user, and a SystemAdmin may belong to no team at all. See docs/password-reset.md.
/// </summary>
public class PasswordResetService(
    AppDbContext dbContext,
    UserManager<User> userManager,
    SystemSettingsService systemSettingsService,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    ILogger<PasswordResetService> logger)
{
    /// <summary>
    /// How long a single email address may go between reset requests. Deliberately coarse: the
    /// threat is using this endpoint to bombard someone's inbox (or to burn SMTP quota), and one
    /// reset every few minutes is well within what a genuinely confused user needs.
    /// </summary>
    public static readonly TimeSpan RequestThrottle = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Always returns <see cref="PasswordResetRequestResult.Accepted"/> for any syntactically usable
    /// email, whether or not an account exists — the caller shows one confirmation either way.
    /// Anything else would turn this page into an account-enumeration oracle: "no such user" tells
    /// an attacker exactly which addresses are worth attacking. The single exception is
    /// <see cref="PasswordResetRequestResult.SystemEmailNotConfigured"/>, which is a deployment
    /// misconfiguration the admin must see rather than a fact about any account.
    /// </summary>
    public async Task<PasswordResetRequestResult> RequestResetAsync(
        string email, Func<int, string, string> resetUrlFactory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return PasswordResetRequestResult.Accepted;
        }

        var settings = await systemSettingsService.GetAsync(cancellationToken);
        if (!settings.IsSystemEmailConfigured)
        {
            // Surfaced, not swallowed: with no sender configured every request would silently do
            // nothing while telling the user to check their inbox.
            logger.LogWarning("Password reset requested but no system SMTP sender is configured — set SystemSettings.SystemSmtp* (Admin -> System Settings)");
            return PasswordResetRequestResult.SystemEmailNotConfigured;
        }

        var user = await userManager.FindByEmailAsync(email);

        // Each of these is a silent no-op that still reports Accepted:
        //  - no such account
        //  - deactivated account. Deactivation here is lockout-based (UserManagementService sets
        //    LockoutEnd to MaxValue), so that is what must be tested — there is no IsActive flag.
        //    A deactivated account must not be resurrectable by whoever holds the mailbox.
        //
        //    User.IsDeactivated tests that sentinel, NOT UserManager.IsLockedOutAsync (#262). The
        //    latter is also true during Identity's ordinary ~5-minute failed-login lockout, so an
        //    attacker who burned five attempts against a known address also switched off that
        //    user's recovery route — while the response stayed "Accepted", correctly non-
        //    enumerable, so the victim waited for mail nobody sent. Someone who has just locked
        //    themselves out by forgetting their password is precisely who needs a reset.
        //  - OAuth-only account with no password hash. Sending a reset there would let anyone with
        //    mailbox access ADD a password to an account that deliberately had none, converting an
        //    SSO-protected login into a password login. They still have working OAuth sign-in.
        if (user is null || user.IsDeactivated || !await userManager.HasPasswordAsync(user))
        {
            logger.LogInformation("Password reset requested for an address with no eligible account — no mail sent (reported as accepted)");
            return PasswordResetRequestResult.Accepted;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (user.LastPasswordResetRequestedUtc is { } last && now - last < RequestThrottle)
        {
            logger.LogInformation("Password reset for user {UserId} throttled — last request {Minutes:F1} min ago", user.Id, (now - last).TotalMinutes);
            return PasswordResetRequestResult.Accepted;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var resetUrl = resetUrlFactory(user.Id, token);

        var message = new EmailMessage(
            ToAddress: user.Email!,
            Subject: "Reset your VE Ops password",
            HtmlBody: BuildBody(user, resetUrl),
            FromAddress: settings.SystemSmtpFromAddress ?? settings.SystemSmtpUsername!,
            FromDisplayName: settings.SystemSmtpFromDisplayName ?? "VE Ops",
            ReplyToAddress: settings.SystemSmtpFromAddress ?? settings.SystemSmtpUsername!);

        // Stamped BEFORE the send, not after: if SMTP is slow or throws, a retry loop must still be
        // throttled. Worst case a failed send costs the user one throttle window.
        user.LastPasswordResetRequestedUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await emailSender.SendAsync(settings.ToSystemEmailCredentials(), message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never surfaced to the caller — a send failure that showed differently in the UI would
            // reintroduce the enumeration oracle this method exists to avoid.
            logger.LogError(ex, "Password reset email failed to send for user {UserId}", user.Id);
            return PasswordResetRequestResult.Accepted;
        }

        // Never log the token or the address — the token is a bearer credential for this account.
        logger.LogInformation("Password reset email sent for user {UserId}", user.Id);
        return PasswordResetRequestResult.Accepted;
    }

    /// <summary>
    /// Completes a reset. Identity validates the token (signature, purpose, expiry, and the user's
    /// current security stamp) — a token is single-use in practice because a successful reset
    /// rotates the stamp, which invalidates it and every other outstanding token for that user.
    /// </summary>
    public async Task<PasswordResetResult> ResetAsync(int userId, string token, string newPassword, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        // Same non-disclosure posture as the request side: an unknown or deactivated user is
        // reported as an invalid token, not as "no such account". IsDeactivated, not
        // IsLockedOutAsync — see the request side (#262). Redeeming a token this user asked for
        // minutes ago must not fail because they then mistyped their old password.
        if (user is null || user.IsDeactivated)
        {
            return new PasswordResetResult(false, ["This reset link is no longer valid. Request a new one."]);
        }

        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            logger.LogInformation("Password reset failed for user {UserId}: {Errors}", user.Id, string.Join("; ", result.Errors.Select(e => e.Code)));
            return new PasswordResetResult(false, result.Errors.Select(e => e.Description).ToList());
        }

        // Clears the throttle so a user who resets successfully isn't blocked from immediately
        // requesting another if something went wrong on their end.
        user.LastPasswordResetRequestedUtc = null;
        dbContext.AddAuditLog(user.Id, "PasswordReset", nameof(User), user.Id,
            "Password reset completed via emailed reset link.", timeProvider.GetUtcNow().UtcDateTime);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Password reset completed for user {UserId}", user.Id);
        return new PasswordResetResult(true, []);
    }

    private static string BuildBody(User user, string resetUrl) =>
        $"""
         <p>Hello {System.Net.WebUtility.HtmlEncode(user.Name)},</p>
         <p>Someone asked to reset the password for your VE Ops account. Use the link
         below to choose a new one:</p>
         <p><a href="{resetUrl}">Reset your password</a></p>
         <p>If you didn't ask for this, you can ignore this email — your password won't change until
         the link above is used.</p>
         """;
}

public enum PasswordResetRequestResult
{
    /// <summary>Request handled. Says nothing about whether an account exists — see RequestResetAsync.</summary>
    Accepted,

    /// <summary>No deployment-wide SMTP sender configured, so no reset email can be sent by anyone.</summary>
    SystemEmailNotConfigured
}

public record PasswordResetResult(bool Succeeded, IReadOnlyList<string> Errors);
