using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VeSessionManager.Core.ExamTools;

/// <summary>
/// HttpClient wrapper for the ExamTools/HamStudy VE API. Auth is a session cookie obtained from
/// POST /api/ve/login (form-urlencoded username/password) — the login endpoint returns HTTP 200
/// even for bad credentials, signalling failure via an {"error": ...} body, so success is judged
/// by the response body, not the status code. Registered as a singleton so the cookie jar
/// survives between poll cycles; expired cookies are handled by one re-login-and-retry.
/// </summary>
public sealed class ExamToolsClient : IExamToolsClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ExamToolsOptions _options;
    private readonly ILogger<ExamToolsClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _loggedIn;

    public ExamToolsClient(IOptions<ExamToolsOptions> options, ILogger<ExamToolsClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            // Long-lived singleton client: recycle pooled connections so DNS changes are picked up.
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        })
        {
            BaseAddress = new Uri(_options.BaseUrl)
        };
    }

    public async Task<IReadOnlyList<ExamToolsSession>> GetTeamSessionsAsync(CancellationToken cancellationToken)
    {
        var sessions = await GetJsonAsync<List<ExamToolsSession>>(
            $"/api/veUser/sessions?team={Uri.EscapeDataString(_options.Team)}", cancellationToken);
        return sessions ?? [];
    }

    public async Task<IReadOnlyList<ExamToolsApplicant>> GetSessionApplicantsAsync(string examToolsSessionId, CancellationToken cancellationToken)
    {
        var export = await GetJsonAsync<ExamToolsApplicantExport>(
            $"/api/veUser/sessions/{Uri.EscapeDataString(examToolsSessionId)}/export/basic.json", cancellationToken);
        return export?.Applicants ?? [];
    }

    private async Task<T?> GetJsonAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(forceRelogin: false, cancellationToken);

        var response = await _httpClient.GetAsync(relativeUrl, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogInformation("ExamTools returned {StatusCode} — session cookie likely expired, re-authenticating", (int)response.StatusCode);
            await EnsureLoggedInAsync(forceRelogin: true, cancellationToken);
            response = await _httpClient.GetAsync(relativeUrl, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private async Task EnsureLoggedInAsync(bool forceRelogin, CancellationToken cancellationToken)
    {
        if (_loggedIn && !forceRelogin)
        {
            return;
        }

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (_loggedIn && !forceRelogin)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password))
            {
                throw new InvalidOperationException(
                    "ExamTools credentials are not configured. Set ExamTools:Username and ExamTools:Password via user-secrets or environment variables.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ve/login");
            // Courtesy convention from the prior client library: identify automated traffic to the ExamTools maintainer.
            request.Headers.Add("Hello-Richard", $"Auto scripting being run by {_options.Username} (VeSessionManager)");
            request.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("username", _options.Username),
                new KeyValuePair<string, string>("password", _options.Password),
                new KeyValuePair<string, string>("remember", "0")
            ]);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"ExamTools login failed: {error.GetString()}");
            }

            _loggedIn = true;
            _logger.LogInformation("Logged into ExamTools at {BaseUrl} as {Username}", _options.BaseUrl, _options.Username);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _loginLock.Dispose();
    }
}
