using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using QRCoder;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Web;

/// <summary>
/// Enrolment mechanics for TOTP two-factor authentication (#356) — the authenticator key, the
/// <c>otpauth://</c> URI, and the QR code drawn from it.
///
/// <para>Separate from the pages because all three have to agree exactly: the key shown as text, the
/// key embedded in the URI, and the key encoded in the QR are the same secret, and a mismatch
/// produces codes that are silently always wrong. One place builds all three.</para>
/// </summary>
public static class TwoFactorSetup
{
    /// <summary>
    /// The label an authenticator app shows. Deployment host plus the account, so someone
    /// administering two VE deployments can tell the entries apart — an authenticator listing two
    /// identical "VE Ops" rows is a support call waiting to happen.
    /// </summary>
    public const string Issuer = "VE Ops";

    /// <summary>Ten, shown once, never again. Identity hashes them; only redemption can check one.</summary>
    public const int RecoveryCodeCount = 10;

    /// <summary>
    /// Fetches the user's authenticator key, generating one on first visit.
    ///
    /// <para>Generated on the enrolment page rather than at account creation on purpose: a key that
    /// exists for every account whether or not it is used is a secret sitting in the database earning
    /// nothing. Re-visiting enrolment reuses the existing key rather than rotating it, so someone who
    /// scanned the QR and then reloaded the page does not end up with an app holding a stale
    /// secret.</para>
    /// </summary>
    public static async Task<string> GetOrCreateKeyAsync(UserManager<User> userManager, User user)
    {
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        await userManager.ResetAuthenticatorKeyAsync(user);
        return await userManager.GetAuthenticatorKeyAsync(user)
            ?? throw new InvalidOperationException("Identity returned no authenticator key after resetting it.");
    }

    /// <summary>Grouped into fours, which is how every authenticator app presents it and how a human
    /// can actually type sixteen characters without losing their place.</summary>
    public static string FormatKeyForDisplay(string key)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            builder.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return builder.ToString().TrimEnd().ToLowerInvariant();
    }

    /// <summary>
    /// The standard <c>otpauth://totp/</c> URI. Both parts are URL-encoded — an account name is an
    /// email address, and an unencoded <c>@</c> or <c>+</c> produces a URI that some apps accept and
    /// others silently mis-parse into a different label.
    /// </summary>
    public static string BuildAuthenticatorUri(string email, string unformattedKey) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            UrlEncoder.Default.Encode(Issuer),
            UrlEncoder.Default.Encode(email),
            unformattedKey);

    /// <summary>
    /// The URI as an inline SVG QR code.
    ///
    /// <para>SVG rather than a PNG data: URI because the CSP allows <c>img-src 'self' data:</c> but
    /// inline markup needs no exception at all — and it stays crisp at any size, which matters when
    /// someone is holding a phone up to a laptop screen.</para>
    ///
    /// <para>ECC level Q (~25% recoverable) rather than L: this is scanned off a screen at an angle,
    /// often a glossy one, and the size cost is a few hundred bytes of markup.</para>
    ///
    /// <para><b>Sized here as well as in CSS, deliberately.</b> QRCoder writes literal
    /// <c>width</c>/<c>height</c> attributes, and the first version left those at 305px and relied on
    /// <c>.qr-holder svg</c> to scale it down — which did not take effect on the deployed page, so the
    /// code rendered at 305px and crowded the step it belongs to. Rather than chase why one CSS rule
    /// lost, the intrinsic size is now correct on its own and the CSS is belt-and-braces. A QR is one
    /// of the few things where "renders bigger than intended" is harmless and "renders smaller than
    /// intended" is not scannable, so erring on the generous side of the CSS is the right way round.</para>
    /// </summary>
    public static string BuildQrCodeSvg(string authenticatorUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(authenticatorUri, QRCodeGenerator.ECCLevel.Q);
        return new SvgQRCode(data).GetGraphic(
            // 61 modules at 3px plus quiet zones lands a little under 200px — big enough to scan
            // off a laptop screen, small enough to sit inside a step rather than dominate the page.
            pixelsPerModule: 3,
            darkColorHex: "#000000",
            lightColorHex: "#ffffff",
            drawQuietZones: true);
    }

    /// <summary>
    /// Strips the spaces and hyphens people paste in from an authenticator app's display, so a code
    /// copied as "123 456" verifies instead of being rejected as wrong — which reads as "my
    /// authenticator is broken", not "I typed a space".
    ///
    /// <para><b>For TOTP codes only.</b> Identity's recovery codes contain a hyphen, so putting them
    /// through this makes a correctly-copied code fail to redeem. The challenge page trims those
    /// instead — see its remarks.</para>
    /// </summary>
    public static string NormalizeCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Trim();
}
