using MimeKit;
using VeSessionManager.Core.Email;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <see cref="SmtpEmailSender.BuildMimeMessage"/>'s attachment handling (#491) — made <c>internal</c>
/// (the project already has <c>InternalsVisibleTo</c> for this test assembly) specifically so this can
/// be asserted without standing up a real SMTP server.
///
/// <para><b>A real MimeKit Attachment, not a LinkedResource.</b> See
/// <see cref="EmailMessage.IcsAttachment"/> — the distinction from <see cref="InlineImage"/> is the
/// whole point: an .ics needs to be downloadable/openable, which only <c>BodyBuilder.Attachments</c>
/// produces.</para>
/// </summary>
public class SmtpEmailSenderAttachmentTests
{
    private static EmailMessage NewMessage(EmailAttachment? attachment = null) => new(
        "candidate@example.com", "noreply@example.org", "VE Ops", "reply@example.org",
        "Subject", "<p>Body</p>", IcsAttachment: attachment);

    [Fact]
    public void NoIcsAttachment_ProducesNoAttachmentPart()
    {
        var mime = SmtpEmailSender.BuildMimeMessage(NewMessage());

        Assert.Empty(mime.Attachments);
    }

    [Fact]
    public void IcsAttachment_ProducesARealDownloadableAttachment_NotALinkedResource()
    {
        var ics = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray();
        var attachment = new EmailAttachment("invite.ics", "text/calendar; method=PUBLISH; charset=utf-8", ics);

        var mime = SmtpEmailSender.BuildMimeMessage(NewMessage(attachment));

        var part = Assert.IsAssignableFrom<MimePart>(Assert.Single(mime.Attachments));
        Assert.Equal("invite.ics", part.FileName);
        Assert.Equal("calendar", part.ContentType.MediaSubtype);
        Assert.True(part.IsAttachment);

        using var stream = new MemoryStream();
        Assert.NotNull(part.Content);
        part.Content!.DecodeTo(stream);
        Assert.Equal(ics, stream.ToArray());
    }
}
