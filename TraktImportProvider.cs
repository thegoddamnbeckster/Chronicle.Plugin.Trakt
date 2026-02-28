using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Trakt;

/// <summary>
/// Chronicle import plugin for Trakt.tv.
///
/// Authentication uses Trakt's OAuth device/PIN flow:
///   1. Call StartAuthAsync() → show UserCode + VerificationUrl to user.
///   2. Poll PollAuthAsync() every PollingIntervalSeconds until Authorized.
///   3. On Authorized, Chronicle persists the tokens via PluginService.UpdateSettingsAsync.
///
/// Import:
///   - Watch history  (paginated, respects ?since= for incremental syncs)
///   - Ratings        (movies + shows)
///   - Watchlist      (movies + shows)
///
/// Rate limits: 1,000 requests / 5-minute window. The internal TraktClient
/// tracks remaining quota and pauses automatically when the window is exhausted.
///
/// Required settings: client_id, client_secret
/// Persisted post-auth: access_token, refresh_token, access_token_expires_at (Unix seconds)
/// </summary>
public sealed class TraktImportProvider : IImportProvider
{
    // ── IImportProvider identity ──────────────────────────────────────────────

    public string PluginId    => "trakt";
    public string Name        => "Trakt";
    public string Version     => "1.0.0";
    public string Author      => "Michael Beck";
    public string Description => "Import watch history, ratings and watchlist from Trakt.tv";

    // ── Settings keys (constants to avoid typos) ──────────────────────────────

    private const string KeyClientId             = "client_id";
    private const string KeyClientSecret         = "client_secret";
    private const string KeyAccessToken          = "access_token";
    private const string KeyRefreshToken         = "refresh_token";
    private const string KeyAccessTokenExpiresAt = "access_token_expires_at";

    // ── Runtime state (set by Configure) ─────────────────────────────────────

    private string? _clientId;
    private string? _clientSecret;
    private string? _accessToken;
    private string? _refreshToken;
    private long    _accessTokenExpiresAt;  // Unix seconds

    // Lazy-created client; replaced when settings change.
    private TraktClient? _client;

    // ── Settings schema ───────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = KeyClientId,
                Label       = "Client ID",
                Description = "Your Trakt application client ID from https://trakt.tv/oauth/applications",
                Type        = SettingType.Text,
                Required    = true,
            },
            new SettingDefinition
            {
                Key         = KeyClientSecret,
                Label       = "Client Secret",
                Description = "Your Trakt application client secret",
                Type        = SettingType.Password,
                Required    = true,
            },
            // The following are written back by Chronicle after successful auth —
            // they are not filled in by the user directly.
            new SettingDefinition
            {
                Key         = KeyAccessToken,
                Label       = "Access Token",
                Description = "Stored automatically after authentication. Do not edit manually.",
                Type        = SettingType.Password,
                Required    = false,
            },
            new SettingDefinition
            {
                Key         = KeyRefreshToken,
                Label       = "Refresh Token",
                Description = "Stored automatically after authentication. Do not edit manually.",
                Type        = SettingType.Password,
                Required    = false,
            },
            new SettingDefinition
            {
                Key         = KeyAccessTokenExpiresAt,
                Label       = "Token Expires At (Unix)",
                Description = "Stored automatically. Unix timestamp of access token expiry.",
                Type        = SettingType.Text,
                Required    = false,
            },
        ]
    };

    // ── Configure ─────────────────────────────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyClientId,             out _clientId);
        settings.TryGetValue(KeyClientSecret,         out _clientSecret);
        settings.TryGetValue(KeyAccessToken,          out _accessToken);
        settings.TryGetValue(KeyRefreshToken,         out _refreshToken);

        if (settings.TryGetValue(KeyAccessTokenExpiresAt, out var expiresStr))
            long.TryParse(expiresStr, out _accessTokenExpiresAt);

        // Recreate the HTTP client with the new credentials.
        _client?.Dispose();
        _client = string.IsNullOrWhiteSpace(_clientId)
            ? null
            : new TraktClient(_clientId, _accessToken);
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    public async Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default)
    {
        EnsureClientId();
        var client   = GetOrCreateClient();
        var response = await client.RequestDeviceCodeAsync(_clientId!, ct);

        return new DeviceAuthStart(
            UserCode:               response.UserCode,
            VerificationUrl:        response.VerificationUrl,
            ExpiresInSeconds:       response.ExpiresIn,
            PollingIntervalSeconds: response.Interval,
            PollCode:               response.DeviceCode   // Chronicle passes this back to PollAuthAsync
        );
    }

    public async Task<DeviceAuthPollResult> PollAuthAsync(
        string pollCode, CancellationToken ct = default)
    {
        EnsureClientId();
        var client = GetOrCreateClient();

        try
        {
            var token = await client.PollDeviceTokenAsync(pollCode, _clientId!, _clientSecret!, ct);

            if (token is null)
                return new DeviceAuthPollResult(DeviceAuthStatus.Pending);

            // Auth succeeded — package the tokens for Chronicle to persist.
            var expiresAt = token.CreatedAt + token.ExpiresIn;

            return new DeviceAuthPollResult(
                Status: DeviceAuthStatus.Authorized,
                NewSettings: new Dictionary<string, string>
                {
                    [KeyAccessToken]          = token.AccessToken,
                    [KeyRefreshToken]         = token.RefreshToken,
                    [KeyAccessTokenExpiresAt] = expiresAt.ToString(),
                });
        }
        catch (TraktAuthException ex) when (ex.Reason == "expired")
        {
            return new DeviceAuthPollResult(DeviceAuthStatus.Expired,
                ErrorMessage: "The device code has expired. Start auth again.");
        }
        catch (TraktAuthException ex) when (ex.Reason == "denied")
        {
            return new DeviceAuthPollResult(DeviceAuthStatus.Denied,
                ErrorMessage: "The user denied authorization.");
        }
        catch (TraktAuthException ex)
        {
            return new DeviceAuthPollResult(DeviceAuthStatus.Denied,
                ErrorMessage: ex.Message);
        }
    }

    public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
            return Task.FromResult(false);

        // Check local expiry — tokens last 3 months; we treat them as expired 1 day early.
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_accessTokenExpiresAt > 0 && nowUnix >= _accessTokenExpiresAt - 86400)
            return Task.FromResult(false);

        return Task.FromResult(true);
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    public ImportCapabilities GetCapabilities() =>
        new(SupportsHistory: true, SupportsRatings: true, SupportsWatchlist: true);

    // ── Import — history ──────────────────────────────────────────────────────

    public async Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var client = GetOrCreateClient();
        var result = new List<ImportedWatchEvent>();

        // Trakt paginates history; fetch all pages.
        var page = 1;
        int totalPages;

        do
        {
            var (items, pages) = await client.GetHistoryPageAsync(since, page, ct);
            totalPages = pages;

            foreach (var entry in items)
            {
                var mapped = MapHistoryEntry(entry);
                if (mapped is not null)
                    result.Add(mapped);
            }

            page++;
        }
        while (page <= totalPages);

        return result;
    }

    // ── Import — ratings ──────────────────────────────────────────────────────

    public async Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var client  = GetOrCreateClient();
        var entries = await client.GetRatingsAsync(ct);
        var result  = new List<ImportedRating>();

        foreach (var r in entries)
        {
            var mapped = MapRatingEntry(r);
            if (mapped is not null)
                result.Add(mapped);
        }

        return result;
    }

    // ── Import — watchlist ────────────────────────────────────────────────────

    public async Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var client  = GetOrCreateClient();
        var entries = await client.GetWatchlistAsync(ct);
        var result  = new List<ImportedWatchlistEntry>();

        foreach (var w in entries)
        {
            var mapped = MapWatchlistEntry(w);
            if (mapped is not null)
                result.Add(mapped);
        }

        return result;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (!await IsAuthenticatedAsync(ct)) return false;
        var client = GetOrCreateClient();
        return await client.PingAsync(ct);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static ImportedWatchEvent? MapHistoryEntry(HistoryEntry entry)
    {
        return entry.Type switch
        {
            "movie" when entry.Movie is not null =>
                new ImportedWatchEvent(
                    ExternalId:     $"trakt:{entry.Movie.Ids.Trakt}",
                    AdditionalIds:  BuildIds(entry.Movie.Ids),
                    MediaType:      "movie",
                    Title:          entry.Movie.Title,
                    Year:           entry.Movie.Year,
                    WatchedAt:      entry.WatchedAt,
                    ProgressPercent: 100.0),

            "episode" when entry.Episode is not null && entry.Show is not null =>
                new ImportedWatchEvent(
                    ExternalId:     $"trakt-episode:{entry.Episode.Ids.Trakt}",
                    AdditionalIds:  BuildIds(entry.Show.Ids, entry.Episode.Ids),
                    MediaType:      "tv_episode",
                    // Use "Show S01E02" as the display title
                    Title:          $"{entry.Show.Title} S{entry.Episode.Season:D2}E{entry.Episode.Number:D2}",
                    Year:           entry.Show.Year,
                    WatchedAt:      entry.WatchedAt,
                    ProgressPercent: 100.0),

            _ => null  // skip unsupported types (seasons, etc.)
        };
    }

    private static ImportedRating? MapRatingEntry(RatingEntry r)
    {
        return r.Type switch
        {
            "movie" when r.Movie is not null =>
                new ImportedRating(
                    ExternalId:    $"trakt:{r.Movie.Ids.Trakt}",
                    AdditionalIds: BuildIds(r.Movie.Ids),
                    MediaType:     "movie",
                    Title:         r.Movie.Title,
                    Year:          r.Movie.Year,
                    Rating:        r.Rating,
                    RatedAt:       r.RatedAt),

            "show" when r.Show is not null =>
                new ImportedRating(
                    ExternalId:    $"trakt:{r.Show.Ids.Trakt}",
                    AdditionalIds: BuildIds(r.Show.Ids),
                    MediaType:     "tv",
                    Title:         r.Show.Title,
                    Year:          r.Show.Year,
                    Rating:        r.Rating,
                    RatedAt:       r.RatedAt),

            _ => null
        };
    }

    private static ImportedWatchlistEntry? MapWatchlistEntry(WatchlistEntry w)
    {
        return w.Type switch
        {
            "movie" when w.Movie is not null =>
                new ImportedWatchlistEntry(
                    ExternalId:    $"trakt:{w.Movie.Ids.Trakt}",
                    AdditionalIds: BuildIds(w.Movie.Ids),
                    MediaType:     "movie",
                    Title:         w.Movie.Title,
                    Year:          w.Movie.Year,
                    AddedAt:       w.ListedAt),

            "show" when w.Show is not null =>
                new ImportedWatchlistEntry(
                    ExternalId:    $"trakt:{w.Show.Ids.Trakt}",
                    AdditionalIds: BuildIds(w.Show.Ids),
                    MediaType:     "tv",
                    Title:         w.Show.Title,
                    Year:          w.Show.Year,
                    AddedAt:       w.ListedAt),

            _ => null
        };
    }

    /// <summary>Builds the cross-reference ID dictionary from Trakt's ids block.</summary>
    private static IReadOnlyDictionary<string, string> BuildIds(
        TraktIds primary, TraktIds? secondary = null)
    {
        var d = new Dictionary<string, string>();

        void Add(TraktIds ids)
        {
            if (ids.Trakt.HasValue)  d["trakt"]  = ids.Trakt.Value.ToString();
            if (ids.Imdb  is not null) d["imdb"]  = ids.Imdb;
            if (ids.Tmdb.HasValue)   d["tmdb"]   = ids.Tmdb.Value.ToString();
            if (ids.Tvdb.HasValue)   d["tvdb"]   = ids.Tvdb.Value.ToString();
        }

        Add(primary);
        if (secondary is not null) Add(secondary);

        return d;
    }

    // ── Guard helpers ─────────────────────────────────────────────────────────

    private void EnsureClientId()
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            throw new InvalidOperationException(
                "Trakt client_id is not configured. " +
                "Set it via Plugins → Trakt → Settings.");
    }

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
            throw new InvalidOperationException(
                "Trakt access token is missing. " +
                "Complete the device auth flow first.");
    }

    private TraktClient GetOrCreateClient()
    {
        if (_client is null)
        {
            EnsureClientId();
            _client = new TraktClient(_clientId!, _accessToken);
        }
        return _client;
    }
}
