using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VeSessionManager.Core.ExamTools;

/// <summary>
/// HttpClient wrapper for the ExamTools/HamStudy VE API. Auth is a session cookie obtained from
/// POST /api/ve/login (form-urlencoded username/password) — the login endpoint returns HTTP 200
/// even for bad credentials, signalling failure via an {"error": ...} body, so success is judged
/// by the response body, not the status code.
///
/// Registered as a singleton, but — unlike the pre-multi-team version of this class — that
/// singleton now manages one independent HttpClient (own CookieContainer)/login-state pair *per
/// team*, keyed by ExamToolsCredentials.TeamId, rather than exactly one for the whole process.
/// This is the template for the deferred Zoom/Discord/Square/Email multi-team fast-follow — see
/// docs/multi-team.md.
/// </summary>
public sealed class ExamToolsClient : IExamToolsClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ExamToolsClient> _logger;
    private readonly ConcurrentDictionary<int, TeamSession> _sessionsByTeamId = new();

    public ExamToolsClient(ILogger<ExamToolsClient> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(ExamToolsCredentials credentials, CancellationToken cancellationToken)
    {
        var sessions = await GetJsonAsync<List<ExamToolsSession>>(
            credentials, $"/api/veUser/sessions?team={Uri.EscapeDataString(credentials.TeamCode)}", cancellationToken);
        return sessions ?? [];
    }

    public async Task<IReadOnlyList<ExamToolsSession>> GetTeamClosedSessionsAsync(
        ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateInclusiveUtc, CancellationToken cancellationToken)
    {
        var sessions = await GetJsonAsync<List<ExamToolsSession>>(
            credentials,
            ClosedSessionsPath(credentials.TeamCode, startDateUtc, endDateInclusiveUtc),
            cancellationToken);
        return sessions ?? [];
    }

    /// <summary>
    /// The closed-sessions URL for an <b>inclusive</b> date range.
    ///
    /// <para><b>ExamTools' end bound is exclusive</b>, verified against the live feed on 2026-08-10:
    /// asking for 2026-04-01..2026-04-30 returned 25 sessions ending on the 29th, while
    /// 2026-04-01..2026-05-01 returned 27 — the two sessions held on the 30th — and still nothing
    /// from 1 May. So the day is added here, and every caller gets the inclusive range it meant.</para>
    ///
    /// <para>Public for its test. The bug this fixes was invisible in every other way: the request
    /// succeeded, the response was valid, and the only symptom was sessions that quietly did not
    /// exist.</para>
    /// </summary>
    public static string ClosedSessionsPath(string teamCode, DateOnly startDateUtc, DateOnly endDateInclusiveUtc) =>
        $"/api/veUser/sessions/{startDateUtc:yyyy-MM-dd}/{endDateInclusiveUtc.AddDays(1):yyyy-MM-dd}?group=all&team={Uri.EscapeDataString(teamCode)}";

    public async Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken)
    {
        // 404 means "this session has no applicant export", which ExamTools returns for a session
        // whose applicants have all cancelled — a legitimate state, not a failure. Found live
        // 2026-07-31: HRCC's last candidate on one session withdrew, and every subsequent ingestion
        // for that whole team threw on this call for two hours. Returning empty also lets the
        // withdrawal detection in SessionIngestionService do its job, which is otherwise impossible
        // for exactly the case it exists to handle — a session emptying out completely.
        var export = await GetJsonAsync<ExamToolsApplicantExport>(
            credentials, $"/api/veUser/sessions/{Uri.EscapeDataString(examToolsSessionId)}/export/basic.json", cancellationToken,
            treatNotFoundAsEmpty: true);
        return export?.Applicants ?? [];
    }

    public async Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken)
    {
        var export = await GetJsonAsync<ExamToolsFullExport>(
            credentials, $"/api/veUser/sessions/{Uri.EscapeDataString(examToolsSessionId)}/export/full.json", cancellationToken);
        return export?.ResolveVes() ?? [];
    }

    public Task<ExamToolsApplicantDetail?> GetApplicantDetailAsync(ExamToolsCredentials credentials, string examToolsSessionId, string applicantId, CancellationToken cancellationToken) =>
        GetJsonAsync<ExamToolsApplicantDetail>(
            credentials, $"/api/veUser/sessions/{Uri.EscapeDataString(examToolsSessionId)}/applicant/{Uri.EscapeDataString(applicantId)}", cancellationToken);

    /// <param name="treatNotFoundAsEmpty">
    /// Return default(T) on a 404 instead of throwing. Only for endpoints where "not found" is a
    /// legitimate state rather than an error — see GetSessionApplicantsAsync.
    /// </param>
    private async Task<T?> GetJsonAsync<T>(ExamToolsCredentials credentials, string relativeUrl, CancellationToken cancellationToken, bool treatNotFoundAsEmpty = false)
    {
        var teamSession = GetOrCreateTeamSession(credentials.TeamId, credentials.BaseUrl);
        await EnsureLoggedInAsync(teamSession, credentials, forceRelogin: false, cancellationToken);

        var response = await teamSession.HttpClient.GetAsync(relativeUrl, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogInformation("ExamTools returned {StatusCode} for team {TeamId} — session cookie likely expired, re-authenticating", (int)response.StatusCode, credentials.TeamId);
            await EnsureLoggedInAsync(teamSession, credentials, forceRelogin: true, cancellationToken);
            response = await teamSession.HttpClient.GetAsync(relativeUrl, cancellationToken);
        }

        if (treatNotFoundAsEmpty && response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("ExamTools returned 404 for {RelativeUrl} (team {TeamId}) — treating as empty rather than an error", relativeUrl, credentials.TeamId);
            return default;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private async Task EnsureLoggedInAsync(TeamSession teamSession, ExamToolsCredentials credentials, bool forceRelogin, CancellationToken cancellationToken)
    {
        if (teamSession.LoggedIn && !forceRelogin)
        {
            return;
        }

        await teamSession.LoginLock.WaitAsync(cancellationToken);
        try
        {
            if (teamSession.LoggedIn && !forceRelogin)
            {
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ve/login");
            // Courtesy convention from the prior client library: identify automated traffic to the ExamTools maintainer.
            request.Headers.Add("Hello-Richard", $"Auto scripting being run by {credentials.Username} (VeSessionManager)");
            request.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("username", credentials.Username),
                new KeyValuePair<string, string>("password", credentials.Password),
                new KeyValuePair<string, string>("remember", "0")
            ]);

            var response = await teamSession.HttpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"ExamTools login failed for team {credentials.TeamId}: {error.GetString()}");
            }

            teamSession.LoggedIn = true;
            // Team id, not username (L-04). This was the single deviation from an otherwise
            // ids-only logging discipline across 175 call sites, and it costs nothing to close: the
            // ExamTools credential is per-team, so the team id already identifies which account was
            // used. The username stays out of log files that are read far more casually than the
            // database is.
            _logger.LogInformation("Logged into ExamTools at {BaseUrl} for team {TeamId}", credentials.BaseUrl, credentials.TeamId);
        }
        finally
        {
            teamSession.LoginLock.Release();
        }
    }

    /// <summary>
    /// Keyed by teamId, but a cached TeamSession whose BaseUrl no longer matches (an admin changed
    /// Team.ExamToolsBaseUrl after this singleton already built one) is torn down and rebuilt rather
    /// than kept — otherwise a base-URL change would silently keep hitting the old host until the
    /// process restarts.
    /// </summary>
    private TeamSession GetOrCreateTeamSession(int teamId, string baseUrl)
    {
        var existing = _sessionsByTeamId.GetOrAdd(teamId, _ => CreateTeamSession(baseUrl));
        if (existing.BaseUrl == baseUrl)
        {
            return existing;
        }

        var replacement = CreateTeamSession(baseUrl);
        _sessionsByTeamId[teamId] = replacement;
        existing.HttpClient.Dispose();
        existing.LoginLock.Dispose();
        return replacement;
    }

    private static TeamSession CreateTeamSession(string baseUrl) =>
        new(new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            // Long-lived cached client: recycle pooled connections so DNS changes are picked up.
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        })
        {
            BaseAddress = new Uri(baseUrl)
        }, baseUrl);

    public void Dispose()
    {
        foreach (var teamSession in _sessionsByTeamId.Values)
        {
            teamSession.HttpClient.Dispose();
            teamSession.LoginLock.Dispose();
        }
    }

    /// <summary>One team's independent cookie jar + login state — see class remarks.</summary>
    private sealed class TeamSession(HttpClient httpClient, string baseUrl)
    {
        public HttpClient HttpClient { get; } = httpClient;
        public string BaseUrl { get; } = baseUrl;
        public SemaphoreSlim LoginLock { get; } = new(1, 1);
        public bool LoggedIn { get; set; }
    }
}
