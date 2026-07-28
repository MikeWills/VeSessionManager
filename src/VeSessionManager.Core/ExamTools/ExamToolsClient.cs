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
        ExamToolsCredentials credentials, DateOnly startDateUtc, DateOnly endDateUtc, CancellationToken cancellationToken)
    {
        var sessions = await GetJsonAsync<List<ExamToolsSession>>(
            credentials,
            $"/api/veUser/sessions/{startDateUtc:yyyy-MM-dd}/{endDateUtc:yyyy-MM-dd}?group=all&team={Uri.EscapeDataString(credentials.TeamCode)}",
            cancellationToken);
        return sessions ?? [];
    }

    public async Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken)
    {
        var export = await GetJsonAsync<ExamToolsApplicantExport>(
            credentials, $"/api/veUser/sessions/{Uri.EscapeDataString(examToolsSessionId)}/export/basic.json", cancellationToken);
        return export?.Applicants ?? [];
    }

    public async Task<IReadOnlyList<ExamToolsVe>> GetSessionVeRosterAsync(ExamToolsCredentials credentials, string examToolsSessionId, CancellationToken cancellationToken)
    {
        var export = await GetJsonAsync<ExamToolsFullExport>(
            credentials, $"/api/veUser/sessions/{Uri.EscapeDataString(examToolsSessionId)}/export/full.json", cancellationToken);
        return export?.Devdoc?.Ves ?? [];
    }

    private async Task<T?> GetJsonAsync<T>(ExamToolsCredentials credentials, string relativeUrl, CancellationToken cancellationToken)
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
            _logger.LogInformation("Logged into ExamTools at {BaseUrl} for team {TeamId} as {Username}", credentials.BaseUrl, credentials.TeamId, credentials.Username);
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
