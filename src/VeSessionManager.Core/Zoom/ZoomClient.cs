using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VeSessionManager.Core.Zoom;

/// <summary>
/// HttpClient wrapper for Zoom's Server-to-Server OAuth flow + Meetings API. Auth is a bearer
/// token from POST https://zoom.us/oauth/token (grant_type=account_credentials, Basic auth of
/// client id/secret) — see https://developers.zoom.us/docs/internal-apps/s2s-oauth/. Tokens
/// expire after an hour with no refresh token, so this caches one and re-requests before expiry.
///
/// Registered as a singleton, but — like ExamToolsClient — that singleton now manages one
/// independent cached access token *per team*, keyed by ZoomCredentials.TeamId, rather than
/// exactly one for the whole process (each team has its own separate Zoom S2S OAuth app). Unlike
/// ExamTools/Discord, Zoom's Bearer-token auth needs no per-team HttpClient/cookie isolation —
/// the same shared HttpClient serves every team's requests, just with a different token looked up
/// per call. See docs/multi-team.md.
/// </summary>
public sealed class ZoomClient : IZoomClient, IDisposable
{
    private const string TokenUrl = "https://zoom.us/oauth/token";
    private const string ApiBaseUrl = "https://api.zoom.us";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ZoomClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<int, TeamZoomSession> _sessionsByTeamId = new();

    public ZoomClient(TimeProvider timeProvider, ILogger<ZoomClient> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        });
    }

    public async Task<ZoomMeeting> CreateMeetingAsync(ZoomCredentials credentials, ZoomMeetingRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            credentials, HttpMethod.Post, $"{ApiBaseUrl}/v2/users/{Uri.EscapeDataString(credentials.UserId)}/meetings",
            ToWireRequest(request), cancellationToken);

        var meeting = await response.Content.ReadFromJsonAsync<ZoomMeetingWireResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Zoom create-meeting response body was empty.");

        _logger.LogInformation("Created Zoom meeting {ZoomMeetingId} for team {TeamId}", meeting.Id, credentials.TeamId);
        return new ZoomMeeting { Id = meeting.Id.ToString(), JoinUrl = meeting.JoinUrl };
    }

    public async Task UpdateMeetingAsync(ZoomCredentials credentials, string meetingId, ZoomMeetingRequest request, CancellationToken cancellationToken)
    {
        await SendAsync(credentials, HttpMethod.Patch, $"{ApiBaseUrl}/v2/meetings/{Uri.EscapeDataString(meetingId)}",
            ToWireRequest(request), cancellationToken);
        _logger.LogInformation("Updated Zoom meeting {ZoomMeetingId} for team {TeamId}", meetingId, credentials.TeamId);
    }

    public async Task DeleteMeetingAsync(ZoomCredentials credentials, string meetingId, CancellationToken cancellationToken)
    {
        await SendAsync(credentials, HttpMethod.Delete, $"{ApiBaseUrl}/v2/meetings/{Uri.EscapeDataString(meetingId)}",
            body: null, cancellationToken);
        _logger.LogInformation("Deleted Zoom meeting {ZoomMeetingId} for team {TeamId}", meetingId, credentials.TeamId);
    }

    public async Task<IReadOnlyList<ZoomMeeting>> ListMeetingsAsync(ZoomCredentials credentials, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            credentials, HttpMethod.Get, $"{ApiBaseUrl}/v2/users/{Uri.EscapeDataString(credentials.UserId)}/meetings?type=scheduled",
            body: null, cancellationToken);

        var list = await response.Content.ReadFromJsonAsync<ZoomMeetingListWireResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Zoom list-meetings response body was empty.");

        return list.Meetings
            .Select(m => new ZoomMeeting { Id = m.Id.ToString(), JoinUrl = m.JoinUrl, Topic = m.Topic, StartTimeUtc = DateTime.SpecifyKind(m.StartTime, DateTimeKind.Utc) })
            .ToList();
    }

    private static ZoomMeetingWireRequest ToWireRequest(ZoomMeetingRequest request) => new()
    {
        Topic = request.Topic,
        // Zoom expects an explicit UTC-suffixed ISO 8601 timestamp; Timezone is separately "UTC" for clarity.
        StartTime = request.StartTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        Duration = request.DurationMinutes,
        Settings = request.BreakoutRoomCount > 0
            ? new ZoomMeetingWireSettings
            {
                BreakoutRoom = new ZoomMeetingWireBreakoutRoom
                {
                    Enable = true,
                    Rooms = Enumerable.Range(1, request.BreakoutRoomCount)
                        .Select(n => new ZoomMeetingWireBreakoutRoomEntry { Name = $"Exam Room {n}" })
                        .ToList()
                }
            }
            : null
    };

    private async Task<HttpResponseMessage> SendAsync(ZoomCredentials credentials, HttpMethod method, string absoluteUrl, object? body, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(credentials, cancellationToken);

        using var request = new HttpRequestMessage(method, absoluteUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken);
        return response;
    }

    private async Task<string> GetAccessTokenAsync(ZoomCredentials credentials, CancellationToken cancellationToken)
    {
        var teamSession = _sessionsByTeamId.GetOrAdd(credentials.TeamId, _ => new TeamZoomSession());

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (teamSession.AccessToken is not null && now < teamSession.TokenExpiresUtc)
        {
            return teamSession.AccessToken;
        }

        await teamSession.TokenLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow().UtcDateTime;
            if (teamSession.AccessToken is not null && now < teamSession.TokenExpiresUtc)
            {
                return teamSession.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(credentials.AccountId) || string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
            {
                // Callers normally check Team.IsZoomConfigured first and never reach here, but the
                // cleanup path (deleting a Zoom meeting for a Cancelled session) always attempts the
                // call regardless of current configuration, since a meeting that already exists
                // needs cleanup even if the team's Zoom setup changed since it was created. A clear
                // exception here (caught and logged by the caller) beats a confusing null-token 401.
                throw new InvalidOperationException(
                    $"Zoom credentials are not configured for team {credentials.TeamId}. Set Team.ZoomAccountId/ZoomClientId/ZoomClientSecret via direct DB edit.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.ClientId}:{credentials.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
            request.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "account_credentials"),
                new KeyValuePair<string, string>("account_id", credentials.AccountId)
            ]);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken);

            var token = await response.Content.ReadFromJsonAsync<ZoomTokenResponse>(JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("Zoom token response body was empty.");

            teamSession.AccessToken = token.AccessToken;
            // Refresh a little early so a call in flight right at expiry never races a 401.
            teamSession.TokenExpiresUtc = now.AddSeconds(Math.Max(token.ExpiresIn - 60, 60));
            _logger.LogInformation("Obtained Zoom Server-to-Server OAuth token for team {TeamId}, expires {ExpiresUtc:u}", credentials.TeamId, teamSession.TokenExpiresUtc);
            return teamSession.AccessToken;
        }
        finally
        {
            teamSession.TokenLock.Release();
        }
    }

    /// <summary>
    /// HttpResponseMessage.EnsureSuccessStatusCode() discards the response body, which for Zoom
    /// is exactly where the useful diagnostic lives (e.g. token errors return
    /// {"reason":"Invalid Client","error":"invalid_client"}; meeting API errors return
    /// {"code":..., "message":"..."}). Never includes request content, so credentials never
    /// end up in a log line or thrown exception.
    /// </summary>
    private static async Task EnsureSuccessOrThrowWithBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Zoom API call to {response.RequestMessage?.RequestUri} failed with {(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    public void Dispose()
    {
        foreach (var teamSession in _sessionsByTeamId.Values)
        {
            teamSession.TokenLock.Dispose();
        }
        _httpClient.Dispose();
    }

    /// <summary>One team's independent cached OAuth token — see class remarks.</summary>
    private sealed class TeamZoomSession
    {
        public string? AccessToken { get; set; }
        public DateTime TokenExpiresUtc { get; set; } = DateTime.MinValue;
        public SemaphoreSlim TokenLock { get; } = new(1, 1);
    }
}
