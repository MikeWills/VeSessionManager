namespace VeSessionManager.Core.Email;

/// <summary>
/// A real, downloadable/openable attachment — as opposed to <see cref="InlineImage"/>, which is
/// referenced from the HTML body by <c>cid:</c> and never offered as a file. Added for #491's
/// calendar invite, and general enough to reuse for any future attachment: unlike an inline image, an
/// .ics has to appear in the recipient's client as a real paperclip, or "add to calendar" never shows.
/// </summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);
