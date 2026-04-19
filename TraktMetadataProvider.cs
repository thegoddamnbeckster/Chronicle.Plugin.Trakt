using System.Text.Json;
using Chronicle.Plugin.Trakt.Models;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Trakt;

/// <summary>
/// Chronicle metadata provider for Trakt.tv.
/// Supports Movies and TV Shows. Requires only a Client ID (no OAuth).
/// Note: Trakt does not provide poster/backdrop images; use TMDB IDs from ExtendedData.
/// </summary>
public sealed class TraktMetadataProvider : IMetadataProvider
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.trakt";
    public string Name     => "Trakt.tv";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── State ─────────────────────────────────────────────────────────────────

    private TraktClient? _client;

    public TraktMetadataProvider() { }

    internal TraktMetadataProvider(TraktClient client) => _client = client;

    // ── Settings ──────────────────────────────────────────────────────────────

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = "client_id",
                Label       = "API Client ID",
                Description = "Your Trakt.tv API Client ID from trakt.tv/oauth/applications",
                Type        = SettingType.Password,
                Required    = true,
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("client_id", out var clientId) ||
            string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Trakt plugin requires 'client_id' to be configured.");

        // clientSecret is only needed for OAuth; pass empty string for metadata-only use.
        _client = new TraktClient(clientId, string.Empty);
    }

    // ── MediaTypeSupport ──────────────────────────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "movie",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "runtime_minutes",
                               "genres", "cast", "directors", "rating"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "movies",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "runtime_minutes",
                               "genres", "cast", "directors", "rating"],
        },
        new MediaTypeSupport
        {
            MediaTypeName   = "tv",
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "runtime_minutes",
                               "genres", "cast", "directors", "rating"],
        },
    ];

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        var traktType = TraktTypeFor(context.MediaTypeName); // "movie" or "show"
        var results   = await _client!.SearchAsync(traktType, context.Name, context.Year, ct);

        var candidates = new List<ScoredCandidate>();
        foreach (var result in results)
        {
            var (title, year, ids, overview, runtime, rating, genres) =
                result.Type == "movie" && result.Movie is { } m
                    ? (m.Title, m.Year, m.Ids, m.Overview, m.Runtime, m.Rating, m.Genres)
                    : result.Show is { } s
                        ? (s.Title, s.Year, s.Ids, s.Overview, s.Runtime, s.Rating, s.Genres)
                        : (null, null, null, null, null, null, null);

            if (ids?.Trakt is not long traktId || title is null) continue;

            var externalId = $"trakt:{result.Type}:{traktId}";
            var meta = new MediaMetadata
            {
                ExternalId     = externalId,
                Source         = "trakt",
                Title          = title,
                Overview       = overview,
                Year           = year,
                RuntimeMinutes = runtime,
                Rating         = rating,
                Genres         = genres ?? [],
                ExtendedData   = JsonSerializer.SerializeToElement(new { ids }),
            };

            var (score, reason) = Score(context, title, year, ids.Imdb, ids.Tmdb?.ToString());
            if (score >= 40)
                candidates.Add(new ScoredCandidate(meta, score, reason));
        }

        return [.. candidates.OrderByDescending(c => c.Score)];
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(
        string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        // Format: trakt:{type}:{id|slug}  e.g. "trakt:movie:348356"
        var parts     = externalId.Split(':', 3);
        if (parts.Length < 3)
            throw new ArgumentException($"Invalid Trakt external ID: {externalId}");

        var traktType  = parts[1]; // "movie" or "show"
        var idOrSlug   = parts[2];
        var pluralType = traktType == "movie" ? "movies" : "shows";

        MediaMetadata meta;
        if (traktType == "movie")
        {
            var full = await _client!.GetMovieAsync(idOrSlug, ct)
                       ?? throw new KeyNotFoundException($"Trakt {externalId} not found.");
            meta = MovieToMetadata(full, externalId);
        }
        else
        {
            var full = await _client!.GetShowAsync(idOrSlug, ct)
                       ?? throw new KeyNotFoundException($"Trakt {externalId} not found.");
            meta = ShowToMetadata(full, externalId);
        }

        // Fetch people (cast + directors) — best-effort.
        try
        {
            var people = await _client!.GetPeopleAsync(pluralType, idOrSlug, ct);
            if (people is not null)
            {
                meta.Cast = people.Cast?
                    .Take(10)
                    .Select(c => c.Person.Name)
                    .ToList() ?? [];
                meta.Directors = people.Crew?.Directing?
                    .Where(d => d.Job?.Equals("Director", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(d => d.Person.Name)
                    .ToList() ?? [];
            }
        }
        catch { /* people call is optional */ }

        return meta;
    }

    // ── Image proxy ───────────────────────────────────────────────────────────

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        return await http.GetByteArrayAsync(url, ct);
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null) return false;
        return await _client.MetadataHealthCheckAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException("TraktMetadataProvider has not been configured.");
    }

    private static string TraktTypeFor(string? mediaTypeName) =>
        mediaTypeName?.ToLowerInvariant() switch
        {
            "movie" or "movies" or "fanedits" => "movie",
            _                                 => "show",
        };

    private static MediaMetadata MovieToMetadata(TraktFullMovie m, string externalId) => new()
    {
        ExternalId     = externalId,
        Source         = "trakt",
        Title          = m.Title,
        Overview       = m.Overview,
        Year           = m.Year,
        RuntimeMinutes = m.Runtime,
        Rating         = m.Rating,
        Genres         = m.Genres ?? [],
        ExtendedData   = JsonSerializer.SerializeToElement(new { ids = m.Ids }),
    };

    private static MediaMetadata ShowToMetadata(TraktFullShow s, string externalId) => new()
    {
        ExternalId     = externalId,
        Source         = "trakt",
        Title          = s.Title,
        Overview       = s.Overview,
        Year           = s.Year,
        RuntimeMinutes = s.Runtime,
        Rating         = s.Rating,
        Genres         = s.Genres ?? [],
        ExtendedData   = JsonSerializer.SerializeToElement(new { ids = s.Ids }),
    };

    private static (int Score, string Reason) Score(
        MediaSearchContext ctx,
        string candidateTitle,
        int? candidateYear,
        string? imdbId,
        string? tmdbId)
    {
        var score = 0;
        var parts = new List<string>();

        if (imdbId is not null && ctx.Name.Contains(imdbId, StringComparison.OrdinalIgnoreCase))
        {
            score += 100; parts.Add("imdb-id-match");
        }
        if (tmdbId is not null && ctx.Name.Contains(tmdbId, StringComparison.OrdinalIgnoreCase))
        {
            score += 100; parts.Add("tmdb-id-match");
        }

        var ctxNorm = Normalise(ctx.Name);
        var canNorm = Normalise(candidateTitle);

        if (ctxNorm == canNorm)             { score += 50; parts.Add("exact-title"); }
        else if (canNorm.Contains(ctxNorm) ||
                 ctxNorm.Contains(canNorm)) { score += 25; parts.Add("partial-title"); }

        if (ctx.Year.HasValue && candidateYear.HasValue && ctx.Year == candidateYear)
        {
            score += 20; parts.Add("year-match");
        }

        return (Math.Min(score, 100), string.Join(", ", parts));
    }

    private static string Normalise(string s) =>
        new string(s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray())
            .Trim();
}
