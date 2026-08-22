using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Plugin.Trakt.Models;
using Serilog;

namespace Chronicle.Plugin.Trakt;

/// <summary>
/// Thin wrapper over the Trakt v2 REST API.
/// One instance per plugin lifetime (recreated when Configure() is called).
/// </summary>
internal sealed class TraktClient : IDisposable
{
    private const string BaseUrl  = "https://api.trakt.tv";
    private const int    PageSize = 500;

    private static readonly ILogger _log = Log.ForContext<TraktClient>();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly string     _clientId;
    private readonly string     _clientSecret;

    private string? _accessToken;
    private string? _refreshToken;
    private long    _tokenExpiresAt;   // Unix seconds

    // ── Construction ─────────────────────────────────────────────────────────

    public TraktClient(string clientId, string clientSecret, HttpClient? httpClient = null)
    {
        _clientId     = clientId;
        _clientSecret = clientSecret;
        _http         = httpClient ?? new HttpClient { BaseAddress = new Uri(BaseUrl) };

        // TryAddWithoutValidation, not Add: Add() runs strict RFC-token validation on the
        // header VALUE for unknown/custom headers, and throws FormatException for content
        // a pasted API key can plausibly contain (e.g. a stray copied character outside the
        // narrow token grammar) — which previously surfaced as an unhandled 500 when saving
        // plugin settings, even though the settings themselves had already saved successfully.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("trakt-api-version", "2");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("trakt-api-key", clientId);
        _http.DefaultRequestHeaders.Accept
             .Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent
             .ParseAdd("Mozilla/5.0 (compatible; Chronicle/1.0)");
    }

    // ── Token management ─────────────────────────────────────────────────────

    public void SetTokens(string accessToken, string refreshToken, long expiresAt)
    {
        _accessToken    = accessToken;
        _refreshToken   = refreshToken;
        _tokenExpiresAt = expiresAt;
    }

    public bool   IsAuthenticated => _accessToken is not null
                                  && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < _tokenExpiresAt;
    public string? AccessToken    => _accessToken;
    public string? RefreshToken   => _refreshToken;
    public long    TokenExpiresAt => _tokenExpiresAt;

    /// <summary>
    /// Fired when a silent token refresh succeeds, with (accessToken, refreshToken, expiresAt).
    /// The host should persist these values so the refreshed token survives a restart.
    /// </summary>
    public event Action<string, string, long>? TokensRefreshed;

    // ── Device auth flow ──────────────────────────────────────────────────────

    public async Task<DeviceCodeResponse> InitiateDeviceAuthAsync(CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(
            "/oauth/device/code",
            new { client_id = _clientId },
            ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Trakt rejected the device auth request (HTTP {(int)response.StatusCode}). " +
                "Check that your Client ID is correct and the app is approved at trakt.tv/oauth/applications.");

        return await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Trakt returned null device-code response.");
    }

    /// <summary>
    /// Polls for token after the user has completed device authorization.
    /// Returns (token, status) where status is one of:
    ///   "authorized" | "pending" | "expired" | "denied" | "slow_down"
    /// </summary>
    public async Task<(TokenResponse? Token, string Status)> PollForTokenAsync(
        string deviceCode, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(
            "/oauth/device/token",
            new
            {
                code          = deviceCode,
                client_id     = _clientId,
                client_secret = _clientSecret
            },
            ct);

        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.OK =>
                (await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOpts, ct), "authorized"),
            System.Net.HttpStatusCode.BadRequest    => (null, "pending"),   // 400 — still waiting
            System.Net.HttpStatusCode.Gone          => (null, "expired"),   // 410
            System.Net.HttpStatusCode.TooManyRequests => (null, "slow_down"), // 429
            _ when (int)response.StatusCode == 418  => (null, "denied"),    // 418 I'm a teapot
            _                                       => (null, "pending")
        };
    }

    // ── Token refresh ─────────────────────────────────────────────────────────

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        if (_refreshToken is null)
            return false;

        try
        {
            var response = await _http.PostAsJsonAsync(
                "/oauth/token",
                new
                {
                    refresh_token = _refreshToken,
                    client_id     = _clientId,
                    client_secret = _clientSecret,
                    redirect_uri  = "urn:ietf:wg:oauth:2.0:oob",
                    grant_type    = "refresh_token"
                },
                ct);

            if (!response.IsSuccessStatusCode)
                return false;

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOpts, ct);
            if (token is null)
                return false;

            _accessToken    = token.AccessToken;
            _refreshToken   = token.RefreshToken;
            _tokenExpiresAt = token.CreatedAt + token.ExpiresIn;
            TokensRefreshed?.Invoke(_accessToken, _refreshToken, _tokenExpiresAt);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures a valid access token is present, refreshing it if within 24 h of expiry.
    /// Throws if no valid token can be obtained.
    /// </summary>
    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken is null)
            throw new InvalidOperationException(
                "Trakt plugin is not authenticated. Complete the device authorization flow first.");

        // Proactively refresh within 24 h of expiry.
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > _tokenExpiresAt - 86_400)
            await TryRefreshAsync(ct);

        if (!IsAuthenticated)
            throw new InvalidOperationException(
                "Trakt access token has expired and could not be refreshed. Re-authenticate.");
    }

    // ── Authenticated request helper ──────────────────────────────────────────

    private HttpRequestMessage AuthGet(string relativeUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return req;
    }

    // ── Data endpoints ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full watch history, paginating via X-Pagination-Page-Count headers.
    /// <paramref name="since"/> restricts results to events after that timestamp.
    /// </summary>
    public async Task<List<TraktHistoryItem>> GetWatchHistoryAsync(
        DateTimeOffset? since, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var all      = new List<TraktHistoryItem>();
        var page     = 1;
        var pageCount = 1;

        do
        {
            var url = since.HasValue
                ? $"/sync/history?limit={PageSize}&page={page}&start_at={Uri.EscapeDataString(since.Value.ToString("O"))}"
                : $"/sync/history?limit={PageSize}&page={page}";

            using var req      = AuthGet(url);
            using var response = await _http.SendAsync(req, ct);

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 429)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    _log.Warning("Trakt rate-limited on history (page {Page}); waiting {Seconds}s",
                        page, (int)wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;   // retry same page
                }
                _log.Warning("Trakt GetWatchHistory failed: {Status} (page {Page})",
                    (int)response.StatusCode, page);
                break;
            }

            if (page == 1)
                pageCount = ReadPageCount(response);

            var items = await response.Content
                .ReadFromJsonAsync<List<TraktHistoryItem>>(JsonOpts, ct);

            if (items is null || items.Count == 0)
                break;

            all.AddRange(items);

            if (page < pageCount)
                await Task.Delay(100, ct);   // Be respectful of Trakt's rate limits.

            page++;
        }
        while (page <= pageCount);

        return all;
    }

    public async Task<List<TraktRatingItem>> GetRatingsAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var all      = new List<TraktRatingItem>();
        var page     = 1;
        var pageCount = 1;

        do
        {
            using var req      = AuthGet($"/sync/ratings?limit={PageSize}&page={page}");
            using var response = await _http.SendAsync(req, ct);

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 429)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    _log.Warning("Trakt rate-limited on ratings (page {Page}); waiting {Seconds}s",
                        page, (int)wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
                }
                _log.Warning("Trakt GetRatings failed: {Status} (page {Page})",
                    (int)response.StatusCode, page);
                break;
            }

            if (page == 1)
                pageCount = ReadPageCount(response);

            var items = await response.Content
                .ReadFromJsonAsync<List<TraktRatingItem>>(JsonOpts, ct);

            if (items is null || items.Count == 0) break;
            all.AddRange(items);
            if (page < pageCount) await Task.Delay(100, ct);
            page++;
        }
        while (page <= pageCount);

        return all;
    }

    public async Task<List<TraktWatchlistItem>> GetWatchlistAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var all      = new List<TraktWatchlistItem>();
        var page     = 1;
        var pageCount = 1;

        do
        {
            using var req      = AuthGet($"/sync/watchlist?limit={PageSize}&page={page}");
            using var response = await _http.SendAsync(req, ct);

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 429)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                    _log.Warning("Trakt rate-limited on watchlist (page {Page}); waiting {Seconds}s",
                        page, (int)wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
                }
                _log.Warning("Trakt GetWatchlist failed: {Status} (page {Page})",
                    (int)response.StatusCode, page);
                break;
            }

            if (page == 1)
                pageCount = ReadPageCount(response);

            var items = await response.Content
                .ReadFromJsonAsync<List<TraktWatchlistItem>>(JsonOpts, ct);

            if (items is null || items.Count == 0) break;
            all.AddRange(items);
            if (page < pageCount) await Task.Delay(100, ct);
            page++;
        }
        while (page <= pageCount);

        return all;
    }

    private static int ReadPageCount(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Pagination-Page-Count", out var vals)
            && int.TryParse(vals.FirstOrDefault(), out var n) && n > 0)
            return n;
        return 1;
    }

    /// <summary>Verifies the access token is valid by calling /users/me.</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        if (_accessToken is null)
            return false;

        try
        {
            await EnsureTokenAsync(ct);
            using var req      = AuthGet("/users/me");
            using var response = await _http.SendAsync(req, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Metadata endpoints ────────────────────────────────────────────────────

    public async Task<List<TraktSearchResult>> SearchAsync(
        string type, string query, int? year, CancellationToken ct)
    {
        var url = $"/search/{type}?query={Uri.EscapeDataString(query)}&extended=full";
        if (year.HasValue) url += $"&years={year}";

        using var response = await GetWithRetryAsync(url, ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            _log.Warning("Trakt search failed: {Status} for query={Query} type={Type}",
                (int?)response?.StatusCode ?? 0, query, type);
            return [];
        }

        return await response.Content
            .ReadFromJsonAsync<List<TraktSearchResult>>(JsonOpts, ct) ?? [];
    }

    /// <summary>
    /// Looks up a Trakt item by a foreign ID (IMDB or TMDB).
    /// <paramref name="idSource"/> should be "imdb" or "tmdb".
    /// Returns the first matching result, or null when not found.
    /// </summary>
    public async Task<TraktSearchResult?> SearchByIdAsync(
        string idSource, string idValue, string? mediaType, CancellationToken ct)
    {
        var url = $"/search/{idSource}/{Uri.EscapeDataString(idValue)}?extended=full";
        if (mediaType is not null) url += $"&type={mediaType}";

        using var response = await GetWithRetryAsync(url, ct);
        if (response is null || !response.IsSuccessStatusCode) return null;
        var results = await response.Content
            .ReadFromJsonAsync<List<TraktSearchResult>>(JsonOpts, ct);
        return results?.FirstOrDefault();
    }

    public async Task<TraktFullMovie?> GetMovieAsync(string idOrSlug, CancellationToken ct)
    {
        using var response = await GetWithRetryAsync($"/movies/{idOrSlug}?extended=full", ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            _log.Warning("Trakt GetMovie failed: {Status} for id={Id}",
                (int?)response?.StatusCode ?? 0, idOrSlug);
            return null;
        }
        return await response.Content.ReadFromJsonAsync<TraktFullMovie>(JsonOpts, ct);
    }

    public async Task<TraktFullShow?> GetShowAsync(string idOrSlug, CancellationToken ct)
    {
        using var response = await GetWithRetryAsync($"/shows/{idOrSlug}?extended=full", ct);
        if (response is null || !response.IsSuccessStatusCode)
        {
            _log.Warning("Trakt GetShow failed: {Status} for id={Id}",
                (int?)response?.StatusCode ?? 0, idOrSlug);
            return null;
        }
        return await response.Content.ReadFromJsonAsync<TraktFullShow>(JsonOpts, ct);
    }

    public async Task<TraktPeopleResponse?> GetPeopleAsync(
        string pluralType, string idOrSlug, CancellationToken ct)
    {
        using var response = await GetWithRetryAsync($"/{pluralType}/{idOrSlug}/people", ct);
        if (response is null || !response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<TraktPeopleResponse>(JsonOpts, ct);
    }

    /// <summary>
    /// GET with one automatic retry on HTTP 429, respecting the Retry-After header.
    /// </summary>
    private async Task<HttpResponseMessage?> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await _http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
                return response;

            if ((int)response.StatusCode == 429 && attempt == 0)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                _log.Warning("Trakt rate-limited (429); waiting {Seconds}s before retry",
                    (int)wait.TotalSeconds);
                response.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }

            return response;
        }

        return null;
    }

    public async Task<bool> MetadataHealthCheckAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("/movies/trending", ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warning("Trakt metadata health check failed: HTTP {Status} (client_id prefix: {Prefix})",
                    (int)response.StatusCode,
                    _clientId.Length > 8 ? _clientId[..8] + "…" : "(empty)");

                // /movies/trending needs no user OAuth -- only a valid client_id -- so a 401/403
                // here means Trakt itself is rejecting the configured client_id (revoked, wrong,
                // or the Trakt API application was disabled), not "not authenticated". Throwing
                // instead of returning false lets PluginService.HealthCheckAsync's own exception
                // classifier surface that specific, actionable reason instead of the generic
                // "Health check returned unhealthy." every other transient failure falls back to.
                if ((int)response.StatusCode is 401 or 403)
                    throw new InvalidOperationException(
                        $"Trakt rejected the configured client_id (HTTP {(int)response.StatusCode}) -- " +
                        "check the API application on trakt.tv/oauth/applications and re-enter its Client ID.");
            }
            return response.IsSuccessStatusCode;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Trakt metadata health check threw an exception");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
