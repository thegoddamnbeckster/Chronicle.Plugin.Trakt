using System.Text.Json.Serialization;

namespace Chronicle.Plugin.Trakt;

// ── Device auth ───────────────────────────────────────────────────────────────

internal record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")]   string DeviceCode,
    [property: JsonPropertyName("user_code")]     string UserCode,
    [property: JsonPropertyName("verification_url")] string VerificationUrl,
    [property: JsonPropertyName("expires_in")]    int    ExpiresIn,
    [property: JsonPropertyName("interval")]      int    Interval
);

internal record DeviceTokenResponse(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("token_type")]    string TokenType,
    [property: JsonPropertyName("expires_in")]    int    ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("scope")]         string Scope,
    [property: JsonPropertyName("created_at")]    long   CreatedAt
);

internal record RefreshTokenResponse(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("expires_in")]    int    ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("created_at")]    long   CreatedAt
);

// ── Media identifiers ─────────────────────────────────────────────────────────

internal record TraktIds(
    [property: JsonPropertyName("trakt")]  int?    Trakt,
    [property: JsonPropertyName("slug")]   string? Slug,
    [property: JsonPropertyName("imdb")]   string? Imdb,
    [property: JsonPropertyName("tmdb")]   int?    Tmdb,
    [property: JsonPropertyName("tvdb")]   int?    Tvdb
);

// ── History ───────────────────────────────────────────────────────────────────

internal record HistoryMovie(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")]  int?   Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids
);

internal record HistoryShow(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")]  int?   Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids
);

internal record HistoryEpisode(
    [property: JsonPropertyName("season")]  int    Season,
    [property: JsonPropertyName("number")]  int    Number,
    [property: JsonPropertyName("title")]   string? Title,
    [property: JsonPropertyName("ids")]     TraktIds Ids
);

internal record HistoryEntry(
    [property: JsonPropertyName("id")]          long            Id,
    [property: JsonPropertyName("watched_at")]  DateTimeOffset  WatchedAt,
    [property: JsonPropertyName("action")]      string          Action,
    [property: JsonPropertyName("type")]        string          Type,
    [property: JsonPropertyName("movie")]       HistoryMovie?   Movie,
    [property: JsonPropertyName("show")]        HistoryShow?    Show,
    [property: JsonPropertyName("episode")]     HistoryEpisode? Episode
);

// ── Ratings ───────────────────────────────────────────────────────────────────

internal record RatingMovie(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")]  int?   Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids
);

internal record RatingShow(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")]  int?   Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids
);

internal record RatingEntry(
    [property: JsonPropertyName("rated_at")] DateTimeOffset RatedAt,
    [property: JsonPropertyName("rating")]   int            Rating,
    [property: JsonPropertyName("type")]     string         Type,
    [property: JsonPropertyName("movie")]    RatingMovie?   Movie,
    [property: JsonPropertyName("show")]     RatingShow?    Show
);

// ── Watchlist ─────────────────────────────────────────────────────────────────

internal record WatchlistMovie(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")]  int?   Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids
);

internal record WatchlistShow(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")]  int?   Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids
);

internal record WatchlistEntry(
    [property: JsonPropertyName("listed_at")]  DateTimeOffset    ListedAt,
    [property: JsonPropertyName("type")]       string            Type,
    [property: JsonPropertyName("movie")]      WatchlistMovie?   Movie,
    [property: JsonPropertyName("show")]       WatchlistShow?    Show
);
