namespace VeSessionManager.Web;

/// <summary>
/// The client-side VE tag filter's "no tags at all" sentinel, shared by the two screens that pick VEs
/// from a list — the session invitation and Email VEs (#394).
///
/// <para>Guests are people with no tag, which is derived rather than stored, so "Untagged" cannot be
/// selected the way a real tag name is and needs a value of its own.</para>
///
/// <para><b>The leading character is a space, and must stay one — it was a literal U+0000 until
/// 2026-08-11 (issue #300), and that silently broke the filter.</b> An HTML parser replaces U+0000
/// with U+FFFD, whether it arrives raw or as <c>&amp;#x0;</c>, so the value JavaScript read back was
/// never the value C# emitted. The equality test in <c>app.js</c> always failed and fell through to
/// "find a tag literally named this", which matches nothing — so choosing "Untagged" hid every VE
/// instead of showing the untagged ones. Verified against a real browser rather than reasoned about;
/// a unit test could not have caught it, because the mangling happens in the parser.</para>
///
/// <para>The literal NUL also made its old home <b>binary to ripgrep</b>, so every search silently
/// skipped that file — which is how the bug survived, and how a code review nearly deleted the DI
/// registration for <c>VeSessionInvitationService</c> on the evidence that nothing referenced it.
/// <c>NoNulBytesInSourceTests</c> now fails the build if a NUL reappears anywhere under src/.</para>
///
/// <para>A real tag can never collide with it: <c>CreateTagAsync</c>/<c>UpdateTagAsync</c>
/// <c>Trim()</c> the name and reject it when blank, so no stored tag can begin with whitespace.
/// <c>VolunteerExaminerDirectoryService.GuestTagFilter</c> is the same trick for the directory's
/// server-side query, with its own value.</para>
///
/// <para><b>Keep this in sync with <c>UNTAGGED</c> in <c>wwwroot/js/app.js</c>.</b> Two copies of one
/// constant with no compiler tying them together; the comment there says the same. Moving it here
/// from <c>VeInviteModel</c> (#394) at least means there is one copy on this side rather than two.</para>
/// </summary>
public static class VeTagFilter
{
    public const string UntaggedValue = " untagged";
}
