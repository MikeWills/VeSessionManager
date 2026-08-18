using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.ExamTools;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// ExamTools answering HTML where JSON was expected (#412).
///
/// <para><b>The failure this prevents.</b> Refresh candidates on beta failed five times running with
/// <c>'&lt;' is an invalid start of a value. Path: $ | LineNumber: 0</c> — <c>System.Text.Json</c>
/// being handed a web page. The client re-authenticated on 401/403 only, and ExamTools answers an
/// unauthenticated or unroutable request with its SPA shell, redirecting a bare fetch to
/// <c>/portal/veLogin</c>; <c>HttpClient</c> follows the redirect, so it arrives as <b>200 with
/// HTML</b>. That sailed past the status check and died in the parser, naming a byte position rather
/// than an endpoint.</para>
/// </summary>
public class ExamToolsHtmlResponseTests
{
    private const string LoginPage = "<!DOCTYPE html><html><body>Sign in to ExamTools</body></html>";
    private const string SessionsJson = """[{"_id":"session-1","date":"2026-08-18T02:30:00Z","vec":"arrl","state":"pend","applicantCount":3}]""";

    /// <summary>Answers the login POST, then serves the queued replies to the feed GET in order.</summary>
    private sealed class FakeExamToolsHandler(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        public int Logins { get; private set; }
        public List<string> Gets { get; } = [];
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/ve/login")
            {
                Logins++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }

            Gets.Add(request.RequestUri!.AbsolutePath);
            var reply = _next < replies.Length ? replies[_next] : replies[^1];
            _next++;
            return Task.FromResult(reply);
        }
    }

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static ExamToolsCredentials Credentials() =>
        new(TeamId: 1, BaseUrl: "https://exam.tools", Username: "ve@example.org", Password: "pw", TeamCode: "TESTTEAM");

    private static ExamToolsClient Create(FakeExamToolsHandler handler) =>
        new(NullLogger<ExamToolsClient>.Instance, handler);

    /// <summary>
    /// The recovery: HTML means "not signed in", which is what a 401 would have meant, so it takes the
    /// same path — one forced re-login and one retry — rather than surfacing a parser error.
    /// </summary>
    [Fact]
    public async Task AnHtmlResponse_ForcesOneReloginAndRetries()
    {
        var handler = new FakeExamToolsHandler(Html(LoginPage), Json(SessionsJson));

        var sessions = await Create(handler).GetTeamSessionsAsync(Credentials(), CancellationToken.None);

        Assert.Single(sessions);
        Assert.Equal("session-1", sessions[0].Id);
        // The first login is the ordinary one; the second is the recovery.
        Assert.Equal(2, handler.Logins);
        Assert.Equal(2, handler.Gets.Count);
    }

    /// <summary>
    /// And when re-authenticating does not help, the error has to say what was asked and what came
    /// back. "'&lt;' is an invalid start of a value … BytePositionInLine: 0" names neither, which is
    /// what made this cost an evening.
    /// </summary>
    [Fact]
    public async Task HtmlThatSurvivesTheRelogin_FailsWithTheUrlAndTheBody()
    {
        var handler = new FakeExamToolsHandler(Html(LoginPage), Html(LoginPage));

        var ex = await Assert.ThrowsAsync<ExamToolsResponseException>(
            () => Create(handler).GetTeamSessionsAsync(Credentials(), CancellationToken.None));

        Assert.Contains("/api/veUser/sessions", ex.Message);
        Assert.Contains("Sign in to ExamTools", ex.Message);
        // It must not retry forever: one recovery attempt, then give up.
        Assert.Equal(2, handler.Gets.Count);
    }

    /// <summary>The ordinary path must not have gained a round trip: valid JSON first time, one login, one GET.</summary>
    [Fact]
    public async Task JsonFirstTime_IsUnaffected()
    {
        var handler = new FakeExamToolsHandler(Json(SessionsJson));

        var sessions = await Create(handler).GetTeamSessionsAsync(Credentials(), CancellationToken.None);

        Assert.Single(sessions);
        Assert.Equal(1, handler.Logins);
        Assert.Single(handler.Gets);
    }
}
