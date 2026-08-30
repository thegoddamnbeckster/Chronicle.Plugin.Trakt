using System.Text.Json.Serialization;

namespace Chronicle.Plugin.Trakt.Models;

// ── Auth flow ─────────────────────────────────────────────────────────────────

internal record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")]    string DeviceCode,
    [property: JsonPropertyName("user_code")]      string UserCode,
    [property: JsonPropertyName("verification_url")] string VerificationUrl,
    [property: JsonPropertyName("expires_in")]     int    ExpiresIn,
    [property: JsonPropertyName("interval")]       int    Interval);

internal record TokenResponse(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("token_type")]    string TokenType,
    [property: JsonPropertyName("expires_in")]    long   ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("scope")]         string Scope,
    [property: JsonPropertyName("created_at")]    long   CreatedAt);

// ── Shared sub-types ──────────────────────────────────────────────────────────

internal record TraktIds(
    [property: JsonPropertyName("trakt")] long?   Trakt,
    [property: JsonPropertyName("slug")]  string? Slug,
    [property: JsonPropertyName("imdb")]  string? Imdb,
    [property: JsonPropertyName("tmdb")]  long?   Tmdb,
    [property: JsonPropertyName("tvdb")]  long?   Tvdb);

internal record TraktAirs(
    [property: JsonPropertyName("day")]      string? Day,
    [property: JsonPropertyName("time")]     string? Time,
    [property: JsonPropertyName("timezone")] string? Timezone);

internal record TraktMovie(
    [property: JsonPropertyName("title")] string   Title,
    [property: JsonPropertyName("year")]  int?     Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids);

internal record TraktShow(
    [property: JsonPropertyName("title")] string   Title,
    [property: JsonPropertyName("year")]  int?     Year,
    [property: JsonPropertyName("ids")]   TraktIds Ids);

internal record TraktEpisode(
    [property: JsonPropertyName("season")] int      Season,
    [property: JsonPropertyName("number")] int      Number,
    [property: JsonPropertyName("title")]  string?  Title,
    [property: JsonPropertyName("ids")]    TraktIds Ids);

// ── History ───────────────────────────────────────────────────────────────────

internal record TraktHistoryItem(
    [property: JsonPropertyName("id")]         long            Id,
    [property: JsonPropertyName("watched_at")] DateTimeOffset  WatchedAt,
    [property: JsonPropertyName("action")]     string          Action,
    [property: JsonPropertyName("type")]       string          Type,
    [property: JsonPropertyName("movie")]      TraktMovie?     Movie,
    [property: JsonPropertyName("show")]       TraktShow?      Show,
    [property: JsonPropertyName("episode")]    TraktEpisode?   Episode);

// ── Ratings ───────────────────────────────────────────────────────────────────

internal record TraktRatingItem(
    [property: JsonPropertyName("rated_at")] DateTimeOffset RatedAt,
    [property: JsonPropertyName("rating")]   int            Rating,
    [property: JsonPropertyName("type")]     string         Type,
    [property: JsonPropertyName("movie")]    TraktMovie?    Movie,
    [property: JsonPropertyName("show")]     TraktShow?     Show,
    [property: JsonPropertyName("episode")]  TraktEpisode?  Episode);

// ── Watchlist ─────────────────────────────────────────────────────────────────

internal record TraktWatchlistItem(
    [property: JsonPropertyName("listed_at")] DateTimeOffset ListedAt,
    [property: JsonPropertyName("id")]        long           Id,
    [property: JsonPropertyName("rank")]      int?           Rank,
    [property: JsonPropertyName("notes")]     string?        Notes,
    [property: JsonPropertyName("type")]      string         Type,
    [property: JsonPropertyName("movie")]     TraktMovie?    Movie,
    [property: JsonPropertyName("show")]      TraktShow?     Show);

// ── Playback progress ────────────────────────────────────────────────────────

/// <summary>One in-progress (not yet finished) item from GET /sync/playback/{movies,episodes}
/// -- distinct from history, which only ever reports completed watches. Progress is 0-100.</summary>
internal record TraktPlaybackItem(
    [property: JsonPropertyName("id")]        long           Id,
    [property: JsonPropertyName("progress")]  double         Progress,
    [property: JsonPropertyName("paused_at")] DateTimeOffset PausedAt,
    [property: JsonPropertyName("type")]      string         Type,
    [property: JsonPropertyName("movie")]     TraktMovie?    Movie,
    [property: JsonPropertyName("show")]      TraktShow?     Show,
    [property: JsonPropertyName("episode")]   TraktEpisode?  Episode);

// ── Search results ────────────────────────────────────────────────────────────

internal record TraktSearchMovie(
    [property: JsonPropertyName("title")]    string        Title,
    [property: JsonPropertyName("year")]     int?          Year,
    [property: JsonPropertyName("ids")]      TraktIds      Ids,
    [property: JsonPropertyName("overview")] string?       Overview,
    [property: JsonPropertyName("runtime")]  int?          Runtime,
    [property: JsonPropertyName("rating")]   double?       Rating,
    [property: JsonPropertyName("genres")]   List<string>? Genres);

internal record TraktSearchShow(
    [property: JsonPropertyName("title")]    string        Title,
    [property: JsonPropertyName("year")]     int?          Year,
    [property: JsonPropertyName("ids")]      TraktIds      Ids,
    [property: JsonPropertyName("overview")] string?       Overview,
    [property: JsonPropertyName("runtime")]  int?          Runtime,
    [property: JsonPropertyName("rating")]   double?       Rating,
    [property: JsonPropertyName("genres")]   List<string>? Genres);

internal record TraktSearchResult(
    [property: JsonPropertyName("type")]  string            Type,
    [property: JsonPropertyName("score")] double?           Score,
    [property: JsonPropertyName("movie")] TraktSearchMovie? Movie,
    [property: JsonPropertyName("show")]  TraktSearchShow?  Show);

// ── Full detail ───────────────────────────────────────────────────────────────

internal record TraktFullMovie(
    [property: JsonPropertyName("title")]         string        Title,
    [property: JsonPropertyName("year")]          int?          Year,
    [property: JsonPropertyName("ids")]           TraktIds      Ids,
    [property: JsonPropertyName("tagline")]       string?       Tagline,
    [property: JsonPropertyName("overview")]      string?       Overview,
    [property: JsonPropertyName("released")]      string?       Released,
    [property: JsonPropertyName("runtime")]       int?          Runtime,
    [property: JsonPropertyName("country")]       string?       Country,
    [property: JsonPropertyName("trailer")]       string?       Trailer,
    [property: JsonPropertyName("homepage")]      string?       Homepage,
    [property: JsonPropertyName("status")]        string?       Status,
    [property: JsonPropertyName("rating")]        double?       Rating,
    [property: JsonPropertyName("certification")] string?       Certification,
    [property: JsonPropertyName("language")]      string?       Language,
    [property: JsonPropertyName("genres")]        List<string>? Genres);

internal record TraktFullShow(
    [property: JsonPropertyName("title")]          string?       Title,
    [property: JsonPropertyName("year")]           int?          Year,
    [property: JsonPropertyName("ids")]            TraktIds      Ids,
    [property: JsonPropertyName("overview")]       string?       Overview,
    [property: JsonPropertyName("first_aired")]    string?       FirstAired,
    [property: JsonPropertyName("airs")]           TraktAirs?    Airs,
    [property: JsonPropertyName("runtime")]        int?          Runtime,
    [property: JsonPropertyName("certification")]  string?       Certification,
    [property: JsonPropertyName("network")]        string?       Network,
    [property: JsonPropertyName("country")]        string?       Country,
    [property: JsonPropertyName("trailer")]        string?       Trailer,
    [property: JsonPropertyName("homepage")]       string?       Homepage,
    [property: JsonPropertyName("status")]         string?       Status,
    [property: JsonPropertyName("rating")]         double?       Rating,
    [property: JsonPropertyName("language")]       string?       Language,
    [property: JsonPropertyName("genres")]         List<string>? Genres,
    [property: JsonPropertyName("aired_episodes")] int?          AiredEpisodes);

// ── People ────────────────────────────────────────────────────────────────────

internal record TraktPerson(
    [property: JsonPropertyName("name")] string    Name,
    [property: JsonPropertyName("ids")]  TraktIds? Ids);

internal record TraktCastMember(
    [property: JsonPropertyName("character")] string?     Character,
    [property: JsonPropertyName("person")]    TraktPerson Person);

internal record TraktCrewMember(
    [property: JsonPropertyName("job")]    string?     Job,
    [property: JsonPropertyName("person")] TraktPerson Person);

internal record TraktCrew(
    [property: JsonPropertyName("directing")] List<TraktCrewMember>? Directing,
    [property: JsonPropertyName("writing")]   List<TraktCrewMember>? Writing,
    [property: JsonPropertyName("production")] List<TraktCrewMember>? Production);

internal record TraktPeopleResponse(
    [property: JsonPropertyName("cast")] List<TraktCastMember>? Cast,
    [property: JsonPropertyName("crew")] TraktCrew?             Crew);
