using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VeSessionManager.Core.Zoom;

/// <summary>
/// HttpClient wrapper for Zoom's Server-to-Server OAuth flow + Meetings API. Auth is a bearer
/// token from POST https://zoom.us/oauth/token (grant_type=account_credentials, Basic auth of
/// client id/secret) — see https://developers.zoom.us/docs/internal-apps/s2s-oauth/. Tokens
/// expire after an hour with no refresh token, so this caches one and re-requests before expiry.
/// Registered as a singleton so the cached token survives between poll cycles.
/// </summary>
public sealed class ZoomClient : IZoomClient, IDisposable
{
    private const string TokenUrl = "https://zoom.us/oauth/token";
    private const string ApiBaseUrl = "https://api.zoom.us";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ZoomOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ZoomClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;

    public ZoomClient(IOptions<ZoomOptions> options, TimeProvider timeProvider, ILogger<ZoomClient> logger)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        });
    }

    public async Task<ZoomMeeting> CreateMeetingAsync(ZoomMeetingRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            HttpMethod.Post, $"{ApiBaseUrl}/v2/users/{Uri.EscapeDataString(_options.UserId)}/meetings",
            ToWireRequest(request), cancellationToken);

        var meeting = await response.Content.ReadFromJsonAsync<ZoomMeetingWireResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Zoom create-meeting response body was empty.");

        _logger.LogInformation("Created Zoom meeting {ZoomMeetingId}", meeting.Id);
        return new ZoomMeeting { Id = meeting.Id.ToString(), JoinUrl = meeting.JoinUrl };
    }

    public async Task UpdateMeetingAsync(string meetingId, ZoomMeetingRequest request, CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Patch, $"{ApiBaseUrl}/v2/meetings/{Uri.EscapeDataString(meetingId)}",
            ToWireRequest(request), cancellationToken);
        _logger.LogInformation("Updated Zoom meeting {ZoomMeetingId}", meetingId);
    }

    public async Task DeleteMeetingAsync(string meetingId, CancellationToken cancellationToken)
    {
        await SendAsync(HttpMethod.Delete, $"{ApiBaseUrl}/v2/meetings/{Uri.EscapeDataString(meetingId)}",
            body: null, cancellationToken);
        _logger.LogInformation("Deleted Zoom meeting {ZoomMeetingId}", meetingId);
    }

    private static ZoomMeetingWireRequest ToWireRequest(ZoomMeetingRequest request) => new()
    {
        Topic = request.Topic,
        // Zoom expects an explicit UTC-suffixed ISO 8601 timestamp; Timezone is separately "UTC" for clarity.
        StartTime = request.StartTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        Duration = request.DurationMinutes
    };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string absoluteUrl, object? body, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);

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

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (_accessToken is not null && now < _tokenExpiresUtc)
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow().UtcDateTime;
            if (_accessToken is not null && now < _tokenExpiresUtc)
            {
                return _accessToken;
            }

            if (string.IsNullOrWhiteSpace(_options.AccountId) || string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ClientSecret))
            {
                throw new InvalidOperationException(
                    "Zoom credentials are not configured. Set Zoom:AccountId, Zoom:ClientId and Zoom:ClientSecret via user-secrets or environment variables.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
            request.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "account_credentials"),
                new KeyValuePair<string, string>("account_id", _options.AccountId)
            ]);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken);

            var token = await response.Content.ReadFromJsonAsync<ZoomTokenResponse>(JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("Zoom token response body was empty.");

            _accessToken = token.AccessToken;
            // Refresh a little early so a call in flight right at expiry never races a 401.
            _tokenExpiresUtc = now.AddSeconds(Math.Max(token.ExpiresIn - 60, 60));
            _logger.LogInformation("Obtained Zoom Server-to-Server OAuth token, expires {ExpiresUtc:u}", _tokenExpiresUtc);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
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
        _httpClient.Dispose();
        _tokenLock.Dispose();
    }
}
