namespace VeSessionManager.Core.ExamTools;

/// <summary>
/// ExamTools answered something that is not JSON, and re-authenticating did not change that (#412).
///
/// <para>Its whole job is to say what a <c>JsonException</c> cannot: which endpoint was asked, and
/// what came back. The parser's own message — <c>'&lt;' is an invalid start of a value. Path: $ |
/// LineNumber: 0 | BytePositionInLine: 0</c> — is true and useless; it reached the ops dashboard five
/// times in one evening without ever naming the request.</para>
///
/// <para>The body snippet is bounded and goes in the message on purpose: this lands in
/// <c>JobRunHistory.ErrorMessage</c>, which is what somebody without shell access actually reads.</para>
/// </summary>
public sealed class ExamToolsResponseException(string relativeUrl, int teamId, string? mediaType, string body)
    : Exception(BuildMessage(relativeUrl, teamId, mediaType, body))
{
    private const int SnippetLength = 200;

    public string RelativeUrl { get; } = relativeUrl;
    public int TeamId { get; } = teamId;

    private static string BuildMessage(string relativeUrl, int teamId, string? mediaType, string body)
    {
        var trimmed = body.Trim();
        var snippet = trimmed.Length <= SnippetLength ? trimmed : trimmed[..SnippetLength] + "...";
        var described = string.IsNullOrWhiteSpace(snippet) ? "(empty body)" : snippet;
        return $"ExamTools returned {mediaType ?? "an unknown content type"} rather than JSON for {relativeUrl} "
             + $"(team {teamId}), and re-authenticating did not help. This is usually its sign-in page. "
             + $"Response began: {described}";
    }
}
