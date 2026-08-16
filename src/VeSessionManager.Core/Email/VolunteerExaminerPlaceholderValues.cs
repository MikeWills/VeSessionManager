using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Email;

/// <summary>
/// The <c>{{Token}}</c> values available to a message written to VEs (#191) — the sibling of
/// <see cref="CandidatePlaceholderValues"/>, for the other audience this app writes to.
///
/// <para><b>No session tokens.</b> The VE invitation screen has <c>{{SessionTitle}}</c>,
/// <c>{{SessionDate}}</c> and <c>{{ZoomJoinUrl}}</c> because it is opened from one session and is
/// about it. This message is not: it is sent from the directory, to whoever was picked, so there is
/// no session to resolve them against and offering them would produce blanks in a real email. Anything
/// about a particular session goes out through that screen instead.</para>
/// </summary>
public static class VolunteerExaminerPlaceholderValues
{
    /// <summary>What the compose screen offers as insertable chips, and the only tokens substituted below.</summary>
    public static readonly IReadOnlyList<string> Names = ["VeName", "CallSign", "TeamName", UnsubscribeUrl];

    /// <summary>
    /// Where the unsubscribe link goes if the author wants it somewhere specific (#191). Optional in
    /// the body: <c>VeMessageService</c> appends a footer carrying it when the draft does not use the
    /// token, because an unsubscribe that depends on somebody remembering to type a placeholder is
    /// one that will eventually be missing from a real send.
    /// </summary>
    public const string UnsubscribeUrl = "UnsubscribeUrl";

    /// <param name="teamName">The team the message is being sent as — passed in rather than derived, because a VE can be on several and only one of them is sending.</param>
    /// <param name="unsubscribeUrl">This recipient's own link. Per-VE, never a shared one.</param>
    public static Dictionary<string, string> For(VolunteerExaminer volunteerExaminer, string teamName, string unsubscribeUrl) => new()
    {
        ["VeName"] = volunteerExaminer.Name,
        ["CallSign"] = volunteerExaminer.CallSign ?? "",
        ["TeamName"] = teamName,
        [UnsubscribeUrl] = unsubscribeUrl
    };
}
