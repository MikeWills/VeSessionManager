using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.Zoom;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// <c>ListMeetingsAsync</c> must return <b>every</b> scheduled meeting, not the first page (#251).
///
/// <para><b>Why a partial list is a correctness bug and not a performance one.</b> This call has one
/// caller and one purpose: it is the query-before-create dedup guard behind
/// <c>SessionEventSchedulingService.FindExistingMeetingAsync</c>. A poll that crashed after Zoom's
/// create succeeded but before the id was persisted asks "does my meeting already exist?" — and a
/// truncated list answers <i>no</i>, so the retry creates a duplicate. That is the exact bug class
/// the guard was built for after the 2026-07-21 Discord incident (~6 real duplicate events).</para>
///
/// <para>It fetched one page and stopped, and Zoom's default page size is 30. The wire DTO had no
/// token field at all, so the truncation was invisible: a team simply stopped being protected once
/// it had 30 scheduled meetings, with nothing anywhere to say the guard had become decorative.</para>
/// </summary>
public class ZoomClientPaginationTests
{
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>
    /// Stands in for Zoom: answers the OAuth token call, then serves canned meeting pages, recording
    /// every URL so the test can assert the token was actually followed rather than inferred from
    /// the result.
    /// </summary>
    private sealed class FakeZoomHandler(params string[] pages) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];
        private int _page;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/oauth/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"access_token":"test-token","expires_in":3600}"""));
            }

            RequestedUrls.Add(url);
            var body = _page < pages.Length ? pages[_page] : """{"meetings":[]}""";
            _page++;
            return Task.FromResult(Json(body));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static ZoomCredentials Credentials() => new(
        TeamId: 1, AccountId: "acct", ClientId: "id", ClientSecret: "secret", UserId: "zoom-user");

    private static ZoomClient Create(FakeZoomHandler handler) =>
        new(new FixedTimeProvider(new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc)),
            NullLogger<ZoomClient>.Instance,
            handler);

    private static string Page(string nextToken, params long[] ids)
    {
        var meetings = string.Join(",", ids.Select(id =>
            $$"""{"id":{{id}},"topic":"Session {{id}}","start_time":"2026-09-01T18:00:00Z","join_url":"https://zoom.example/j/{{id}}"}"""));
        var token = nextToken.Length == 0 ? "" : nextToken;
        return $$"""{"meetings":[{{meetings}}],"next_page_token":"{{token}}"}""";
    }

    /// <summary>The finding itself: a second page existed and was never fetched.</summary>
    [Fact]
    public async Task FollowsNextPageTokenUntilExhausted()
    {
        var handler = new FakeZoomHandler(
            Page("token-2", 1, 2),
            Page("token-3", 3, 4),
            Page("", 5));

        var meetings = await Create(handler).ListMeetingsAsync(Credentials(), CancellationToken.None);

        Assert.Equal(["1", "2", "3", "4", "5"], meetings.Select(m => m.Id));
        Assert.Equal(3, handler.RequestedUrls.Count);
    }

    /// <summary>
    /// The token must actually travel on the request. Returning everything could otherwise be an
    /// accident of the fake serving pages in order, which would pass while the real client asked
    /// Zoom for page one three times.
    /// </summary>
    [Fact]
    public async Task PassesTheTokenBackToZoom()
    {
        var handler = new FakeZoomHandler(Page("token-2", 1), Page("", 2));

        await Create(handler).ListMeetingsAsync(Credentials(), CancellationToken.None);

        Assert.DoesNotContain("next_page_token", handler.RequestedUrls[0]);
        Assert.Contains("next_page_token=token-2", handler.RequestedUrls[1]);
    }

    /// <summary>An empty token is Zoom's "this was the last page" — not a cue to keep asking.</summary>
    [Fact]
    public async Task StopsOnASinglePage()
    {
        var handler = new FakeZoomHandler(Page("", 1, 2));

        var meetings = await Create(handler).ListMeetingsAsync(Credentials(), CancellationToken.None);

        Assert.Equal(2, meetings.Count);
        Assert.Single(handler.RequestedUrls);
    }

    /// <summary>
    /// A token that never clears must not hold the poll forever. Zoom returning a non-advancing
    /// cursor is a remote bug this side cannot fix, but an unbounded loop would turn it into a hung
    /// job — and the whole point of this change is that an incomplete list is never silent, so the
    /// bound logs rather than merely stopping.
    /// </summary>
    [Fact]
    public async Task StopsAtThePageLimitWhenTheTokenNeverClears()
    {
        // Always hands back the same token, so the client would loop indefinitely if unbounded.
        var handler = new FakeZoomHandler([.. Enumerable.Repeat(Page("stuck", 1), 200)]);

        var meetings = await Create(handler).ListMeetingsAsync(Credentials(), CancellationToken.None);

        Assert.NotEmpty(meetings);
        Assert.InRange(handler.RequestedUrls.Count, 1, 60);
    }

    /// <summary>
    /// A token containing URL-significant characters must survive the round trip. Zoom's tokens are
    /// opaque, so this is not hypothetical — an unescaped '&amp;' would silently truncate the query
    /// and the client would ask for page one again, which looks exactly like "no more pages".
    /// </summary>
    [Fact]
    public async Task EscapesTheTokenIntoTheQueryString()
    {
        var handler = new FakeZoomHandler(Page("a+b&c=d", 1), Page("", 2));

        var meetings = await Create(handler).ListMeetingsAsync(Credentials(), CancellationToken.None);

        Assert.Equal(2, meetings.Count);
        Assert.DoesNotContain("&c=d", handler.RequestedUrls[1]);
        Assert.Contains("next_page_token=a%2Bb%26c%3Dd", handler.RequestedUrls[1]);
    }
}
