using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VeSessionManager.Core.ExamTools;
using Xunit;

namespace VeSessionManager.Core.Tests;

/// <summary>
/// Downloading the VEC archive — the file ARRL's upload page asks for (issue #197).
///
/// <para>Every expectation here was taken from a live call on 2026-08-18 against a closed MARC
/// session, not from the API documentation, which does not cover this endpoint at all. See
/// docs/examtools-api.md.</para>
///
/// <para><b>Why this endpoint cannot reuse <c>GetJsonAsync</c>:</b> that helper reads 401/403 as
/// "the cookie expired" and recovers by re-authenticating. Here a <b>403 is a legitimate answer with
/// a meaning of its own</b> — the session is not finished — so treating it as a stale cookie would
/// re-login, retry, get the same 403, and report an authentication problem for something that is
/// nothing of the kind.</para>
/// </summary>
public class VecArchiveDownloadTests
{
    private const string SessionId = "6950a2cbf593f706d2e92247";

    /// <summary>The real body, verbatim from the live 403 against an unfinished session.</summary>
    private const string NotCompleteJson =
        """{"type":"ForbiddenError","message":"Exam Session needs to be completed","data":"","code":403}""";

    private const string LoginPage = "<!DOCTYPE html><html><body>Sign in to ExamTools</body></html>";

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

    /// <summary>A zip response shaped like the real one, optionally with the filename header.</summary>
    private static HttpResponseMessage Zip(byte[] content, string? fileName)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (fileName is not null)
        {
            response.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = $"\"{fileName}\"" };
        }

        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

    private static ExamToolsCredentials Credentials() =>
        new(TeamId: 1, BaseUrl: "https://exam.tools", Username: "ve@example.org", Password: "pw", TeamCode: "MARC");

    private static ExamToolsClient Create(FakeExamToolsHandler handler) =>
        new(NullLogger<ExamToolsClient>.Instance, handler);

    /// <summary>Four bytes standing in for a zip — the client does not inspect the payload.</summary>
    private static byte[] ArchiveBytes() => [0x50, 0x4B, 0x03, 0x04];

    [Fact]
    public async Task ASuccessfulDownload_ReturnsTheBytesAndTheHeaderFilename()
    {
        var handler = new FakeExamToolsHandler(Zip(ArchiveBytes(), "ExamSession_MARC_20260422_0130_arrl.zip"));

        var result = await Create(handler).DownloadVecArchiveAsync(Credentials(), SessionId, "arrl", CancellationToken.None);

        Assert.Equal(VecArchiveDownloadOutcome.Succeeded, result.Outcome);
        Assert.Equal(ArchiveBytes(), result.Content);
        Assert.Equal("ExamSession_MARC_20260422_0130_arrl.zip", result.FileName);
    }

    /// <summary>
    /// The URL's own filename is the generic <c>ExamSession_{vec}_archive.zip</c> — identical for every
    /// session of every team. The descriptive one exists only in the header, which is why
    /// <see cref="VecArchiveDownload.FileName"/> must come from there and never from the request path.
    /// </summary>
    [Fact]
    public async Task TheRequestPath_CarriesTheSessionIdAndTheLowercasedVecCode()
    {
        var handler = new FakeExamToolsHandler(Zip(ArchiveBytes(), "x.zip"));

        await Create(handler).DownloadVecArchiveAsync(Credentials(), SessionId, "ARRL", CancellationToken.None);

        Assert.Equal($"/api/veUser/sessions/{SessionId}/vecDownload/ExamSession_arrl_archive.zip", Assert.Single(handler.Gets));
    }

    /// <summary>
    /// Null, not a guess. The client has no team code or session start to build the descriptive name
    /// from — that is the caller's job (<see cref="VecArchiveFileName"/>), and inventing something
    /// here would produce a plausible-looking wrong filename filed with ARRL.
    /// </summary>
    [Fact]
    public async Task WithNoContentDispositionHeader_TheFilenameIsNull()
    {
        var handler = new FakeExamToolsHandler(Zip(ArchiveBytes(), fileName: null));

        var result = await Create(handler).DownloadVecArchiveAsync(Credentials(), SessionId, "arrl", CancellationToken.None);

        Assert.Equal(VecArchiveDownloadOutcome.Succeeded, result.Outcome);
        Assert.Null(result.FileName);
    }

    /// <summary>
    /// The most common expected failure, and entirely self-correcting — so it is a result, not an
    /// exception, and ExamTools' own wording is carried through for the operator to read.
    /// </summary>
    [Fact]
    public async Task AnUnfinishedSession_IsAResultNotAnException()
    {
        var handler = new FakeExamToolsHandler(Json(HttpStatusCode.Forbidden, NotCompleteJson));

        var result = await Create(handler).DownloadVecArchiveAsync(Credentials(), SessionId, "arrl", CancellationToken.None);

        Assert.Equal(VecArchiveDownloadOutcome.SessionNotComplete, result.Outcome);
        Assert.Equal("Exam Session needs to be completed", result.Message);
        Assert.Null(result.Content);
    }

    /// <summary>
    /// And it must not be mistaken for an expired cookie. A re-login here would retry, collect the
    /// identical 403, and report an authentication failure for a session that simply has not been
    /// closed yet.
    /// </summary>
    [Fact]
    public async Task AnUnfinishedSession_DoesNotTriggerARelogin()
    {
        var handler = new FakeExamToolsHandler(Json(HttpStatusCode.Forbidden, NotCompleteJson));

        await Create(handler).DownloadVecArchiveAsync(Credentials(), SessionId, "arrl", CancellationToken.None);

        Assert.Equal(1, handler.Logins);
        Assert.Single(handler.Gets);
    }

    /// <summary>
    /// An HTML body still means "signed out", exactly as it does for every JSON endpoint (#412) — the
    /// SPA shell arrives with a 200 after HttpClient follows the redirect to /portal/veLogin.
    /// </summary>
    [Fact]
    public async Task AnHtmlResponse_ForcesOneReloginAndRetries()
    {
        var handler = new FakeExamToolsHandler(Html(LoginPage), Zip(ArchiveBytes(), "ExamSession_MARC_20260422_0130_arrl.zip"));

        var result = await Create(handler).DownloadVecArchiveAsync(Credentials(), SessionId, "arrl", CancellationToken.None);

        Assert.Equal(VecArchiveDownloadOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, handler.Logins);
        Assert.Equal(2, handler.Gets.Count);
    }

    [Fact]
    public async Task AnUnexpectedStatus_Throws()
    {
        var handler = new FakeExamToolsHandler(Json(HttpStatusCode.InternalServerError, "{}"));

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => Create(handler).DownloadVecArchiveAsync(Credentials(), SessionId, "arrl", CancellationToken.None));
    }

    /// <summary>
    /// The fallback name, checked against a real ARRL receipt from 2026-04-21 rather than against the
    /// format string that produced it. The session's stored start is 2026-04-22 01:30 UTC — note the
    /// timestamp is <b>UTC</b>, not the session's Eastern calendar date, which is the opposite of the
    /// rule that applies to the form's own sessionDate field.
    /// </summary>
    [Fact]
    public void TheFallbackFilename_MatchesWhatExamToolsItselfSends()
    {
        var built = VecArchiveFileName.Build("MARC", new DateTime(2026, 4, 22, 1, 30, 0, DateTimeKind.Utc), "arrl");

        Assert.Equal("ExamSession_MARC_20260422_0130_arrl.zip", built);
    }

    [Fact]
    public void TheFallbackFilename_LowercasesTheVecCode()
    {
        var built = VecArchiveFileName.Build("MARC", new DateTime(2026, 4, 22, 1, 30, 0, DateTimeKind.Utc), "ARRL");

        Assert.EndsWith("_arrl.zip", built);
    }
}
