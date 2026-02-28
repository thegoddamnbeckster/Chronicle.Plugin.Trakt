using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Chronicle.Plugin.Trakt;

/// <summary>
/// Low-level HTTP wrapper for the Trakt v2 API.
///
/// Rate limits (as of 2024):
///   • 1,000 requests per 5-minute rolling window (~200 req/min)
///   • Response headers carry X-RateLimit-Limit, X-RateLimit-Remaining,
///     X-RateLimit-Reset (Unix timestamp).
///   • When remaining hits 0 we pause until the reset timestamp.
///   • On HTTP 429 we honour the Retry-After header (seconds).
/// </summary>
internal sealed class TraktClient : IDisposable
{
    private const string ApiBase  = "https://api.trakt.tv";
    private const string ApiVersion = "2";

    private readonly HttpClient    _http;
    private readonly SemaphoreSlim _rateLock = new(1, 1);

    // Tracked locally to avoid unnecessary requests when we know we're at 0.
    private int    _remaining    = 1000;
    private long   _resetUnixSec = 0;

    internal TraktClient(string clientId, string? accessToken = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(ApiBase) };
        _http.DefaultRequestHeaders.Add("trakt-api-version", ApiVersion);
        _http.DefaultRequestHeaders.Add("trakt-api-key", clientId);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(accessToken))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
    }

    /// <summary>Updates the Authorization header when a new access token is available.</summary>
    internal void SetAccessToken(string accessToken)
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
    }

    // ── Auth endpoints ────────────────────────────────────────────────────────

    internal async Task<DeviceCodeResponse> RequestDeviceCodeAsync(
        string clientId, CancellationToken ct)
    {
        var body = new { client_id = clientId };
        var response = await _http.PostAsJsonAsync("/oauth/device/code", body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(ct))!;
    }

    /// <summary>
    /// Polls for device token. Returns null when still pending (HTTP 400 / 404).
    /// Throws <see cref="TraktAuthException"/> for expired/denied states.
    /// </summary>
    internal async Task<DeviceTokenResponse?> PollDeviceTokenAsync(
        string deviceCode, string clientId, string clientSecret, CancellationToken ct)
    {
        var body = new { code = deviceCode, client_id = clientId, client_secret = clientSecret };
        var response = await _http.PostAsJsonAsync("/oauth/device/token", body, ct);

        return response.StatusCode switch
        {
            HttpStatusCode.OK         => (await response.Content.ReadFromJsonAsync<DeviceTokenResponse>(ct))!,
            HttpStatusCode.BadRequest => null,   // still pending (400)
            HttpStatusCode.NotFound   => null,   // still pending (404 on some versions)
            HttpStatusCode.Gone       => throw new TraktAuthException("expired"),
            HttpStatusCode.Conflict   => throw new TraktAuthException("already_used"),
            (HttpStatusCode)418       => throw new TraktAuthException("denied"),  // 418 I'm a teapot — Trakt's "denied"
            _                         => throw new TraktAuthException($"unexpected status {(int)response.StatusCode}")
        };
    }

    internal async Task<RefreshTokenResponse> RefreshTokenAsync(
        string refreshToken, string clientId, string clientSecret, CancellationToken ct)
    {
        var body = new
        {
            refresh_token  = refreshToken,
            client_id      = clientId,
            client_secret  = clientSecret,
            grant_type     = "refresh_token"
        };
        var response = await _http.PostAsJsonAsync("/oauth/token", body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RefreshTokenResponse>(ct))!;
    }

    // ── Sync endpoints ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches a single page of watch history.
    /// Returns the items and the total number of pages.
    /// </summary>
    internal async Task<(List<HistoryEntry> Items, int TotalPages)> GetHistoryPageAsync(
        DateTimeOffset? since, int page, CancellationToken ct)
    {
        var url = $"/sync/history?limit=200&page={page}";
        if (since.HasValue)
            url += $"&start_at={Uri.EscapeDataString(since.Value.UtcDateTime.ToString("o"))}";

        var response = await GetWithRateLimitAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var totalPages = 1;
        if (response.Headers.TryGetValues("X-Pagination-Page-Count", out var vals))
            int.TryParse(vals.FirstOrDefault(), out totalPages);

        var items = (await response.Content.ReadFromJsonAsync<List<HistoryEntry>>(ct))
                    ?? new List<HistoryEntry>();
        return (items, totalPages);
    }

    internal async Task<List<RatingEntry>> GetRatingsAsync(CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync("/sync/ratings", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<RatingEntry>>(ct))
               ?? new List<RatingEntry>();
    }

    internal async Task<List<WatchlistEntry>> GetWatchlistAsync(CancellationToken ct)
    {
        var response = await GetWithRateLimitAsync("/sync/watchlist", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<WatchlistEntry>>(ct))
               ?? new List<WatchlistEntry>();
    }

    internal async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            // A lightweight authenticated endpoint — just checks connectivity + token
            var response = await _http.GetAsync("/sync/last_activities", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Rate-limit-aware GET ───────────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetWithRateLimitAsync(
        string url, CancellationToken ct)
    {
        await _rateLock.WaitAsync(ct);
        try
        {
            // If our cached remaining count is 0, wait until the reset window.
            if (_remaining <= 0 && _resetUnixSec > 0)
            {
                var resetAt = DateTimeOffset.FromUnixTimeSeconds(_resetUnixSec);
                var waitMs  = (int)(resetAt - DateTimeOffset.UtcNow).TotalMilliseconds + 500;
                if (waitMs > 0)
                    await Task.Delay(waitMs, ct);
            }

            var response = await _http.GetAsync(url, ct);

            // Update local rate-limit counters from response headers.
            UpdateRateLimitCounters(response.Headers);

            // If we get a 429 anyway, respect the Retry-After header and retry once.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfterSec = 5;
                if (response.Headers.RetryAfter?.Delta.HasValue == true)
                    retryAfterSec = (int)response.Headers.RetryAfter.Delta!.Value.TotalSeconds + 1;

                await Task.Delay(retryAfterSec * 1000, ct);
                response = await _http.GetAsync(url, ct);
                UpdateRateLimitCounters(response.Headers);
            }

            return response;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    private void UpdateRateLimitCounters(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("X-RateLimit-Remaining", out var rem) &&
            int.TryParse(rem.FirstOrDefault(), out var remaining))
            _remaining = remaining;

        if (headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
            long.TryParse(reset.FirstOrDefault(), out var resetTs))
            _resetUnixSec = resetTs;
    }

    public void Dispose()
    {
        _http.Dispose();
        _rateLock.Dispose();
    }
}

internal sealed class TraktAuthException(string reason)
    : Exception($"Trakt auth failed: {reason}")
{
    public string Reason { get; } = reason;
}
