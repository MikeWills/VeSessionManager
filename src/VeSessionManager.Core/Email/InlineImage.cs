namespace VeSessionManager.Core.Email;

/// <summary>
/// An image carried inside the message itself and referenced from the HTML body by
/// <c>&lt;img src="cid:CONTENT-ID"&gt;</c>, rather than fetched from a URL.
///
/// <para><b>Why CID and not a hosted URL.</b> Gmail and Outlook block remote images by default, so a
/// logo pointing at the public site shows as a broken-image placeholder until the recipient clicks
/// "show images" — which most never do. A CID part travels with the message and renders
/// immediately.</para>
/// </summary>
/// <param name="ContentId">
/// The bare id, without the <c>cid:</c> scheme — <see cref="EmailTemplateRenderer"/> writes
/// <c>src="cid:{ContentId}"</c> and the sender labels the MIME part with the same value. They must
/// agree exactly or the image silently fails to resolve in the client.
/// </param>
public sealed record InlineImage(string ContentId, string ContentType, byte[] Content)
{
    /// <summary>
    /// The one id used for a team logo. A constant rather than something generated per send: the
    /// renderer produces the <c>&lt;img&gt;</c> tag and the sender attaches the bytes, in different
    /// classes with no shared state, so a generated value would have to be threaded between them for
    /// no benefit — one message never carries two logos.
    /// </summary>
    public const string TeamLogoContentId = "vesm-team-logo";
}
