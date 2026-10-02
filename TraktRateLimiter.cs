using System.Text.Json;
using Serilog;

namespace Chronicle.Plugin.Trakt;

/// <summary>
/// Client-side limiter that keeps Chronicle under Trakt's own published limits
/// (docs.trakt.tv, "Rate Limiting"): AUTHED_API_GET_LIMIT / UNAUTHED_API_GET_LIMIT = 1,000 GET
/// calls per 5-minute rolling window, AUTHED_API_POST_LIMIT = 1 POST/PUT/DELETE call per second.
/// Unlike Hardcover's daily quota, Trakt's window resets continuously — there is no exhaustion
/// state to circuit-break on, so this is just two token buckets (one per limit category), each
/// kept to ~90% of the published rate for margin.
///
/// Before this, TraktClient paced every paginated GET with a flat 100ms delay (10 req/s) — nearly
/// 3x the ~3.33 req/s a sustained 1,000-per-5-minutes budget actually allows, and there was no
/// distinct, stricter pacing for the (currently OAuth-only) POST calls at all. The plugin isn't
/// loaded in the live Chronicle instance today, so this had not yet produced real 429s, but it
/// needed fixing before that changes.
/// </summary>
internal sealed class TraktRateLimiter
{
    // 1,000 GET/5min = 3.333/s; keep sustained throughput at 90% of that with a short burst
    // allowance for pagination loops that arrive faster than the refill rate.
    public const double GetBucketCapacity  = 30;
    public const double GetRefillPerSecond = 3.0;

    // 1 POST|PUT|DELETE/s; keep sustained throughput at ~90% of that. Capacity 1 makes this a
    // plain min-interval gate (no burst) since Trakt's write limit has no separate burst allowance.
    public const double WriteBucketCapacity  = 1;
    public const double WriteRefillPerSecond = 0.9;

    private static readonly ILogger Log = Serilog.Log.ForContext<TraktRateLimiter>();

    private readonly Bucket _get;
    private readonly Bucket _write;

    public TraktRateLimiter(Func<DateTime>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        utcNow ??= () => DateTime.UtcNow;
        delay  ??= (t, ct) => Task.Delay(t, ct);
        _get   = new Bucket("GET", GetBucketCapacity, GetRefillPerSecond, utcNow, delay);
        _write = new Bucket("POST/PUT/DELETE", WriteBucketCapacity, WriteRefillPerSecond, utcNow, delay);
    }

    /// <summary>The process-wide instance shared by every TraktClient (one account, one quota).</summary>
    public static TraktRateLimiter Shared { get; } = new();

    public double GetTokens   => _get.Tokens;
    public double WriteTokens => _write.Tokens;

    private static bool IsWrite(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Delete;

    private Bucket BucketFor(HttpMethod method) => IsWrite(method) ? _write : _get;

    /// <summary>Takes one token from the bucket matching this method, waiting for a refill when empty.</summary>
    public Task AcquireAsync(HttpMethod method, CancellationToken ct) => BucketFor(method).AcquireAsync(ct);

    /// <summary>A 429 just arrived for this method's bucket: drain it and pause every caller of that
    /// category for <paramref name="pause"/> (the server's Retry-After plus a margin).</summary>
    public void NoteThrottled(HttpMethod method, TimeSpan pause) => BucketFor(method).NoteThrottled(pause);

    /// <summary>
    /// Applies Trakt's own X-Ratelimit header when present (sent on 429 responses):
    /// {"name":"...","period":300,"limit":1000,"remaining":941,"until":"..."}. Clamps the bucket
    /// matching this request's method down to what the server says is actually left. Never throws
    /// — a header that fails to parse is simply ignored.
    /// </summary>
    public void Observe(HttpMethod method, IEnumerable<string> rateLimitHeaderValues)
    {
        try
        {
            foreach (var raw in rateLimitHeaderValues)
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("remaining", out var remainingEl)) continue;
                if (!remainingEl.TryGetInt32(out var remaining)) continue;
                BucketFor(method).Observe(remaining);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Trakt X-Ratelimit header could not be parsed; ignoring it");
        }
    }

    private sealed class Bucket
    {
        private readonly string _name;
        private readonly double _capacity;
        private readonly double _refillPerSecond;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _lock = new();

        private double   _tokens;
        private DateTime _lastRefill;
        private DateTime _pausedUntil = DateTime.MinValue;

        public Bucket(string name, double capacity, double refillPerSecond,
            Func<DateTime> utcNow, Func<TimeSpan, CancellationToken, Task> delay)
        {
            _name = name;
            _capacity = capacity;
            _refillPerSecond = refillPerSecond;
            _utcNow = utcNow;
            _delay = delay;
            _tokens = capacity;
            _lastRefill = utcNow();
        }

        public double Tokens { get { lock (_lock) return _tokens; } }

        public async Task AcquireAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var wait = TimeSpan.Zero;
                lock (_lock)
                {
                    Refill();
                    if (_tokens < 1) wait = TimeSpan.FromSeconds((1 - _tokens) / _refillPerSecond);
                    // A 429 pauses EVERY caller in this category, not just the one that received it --
                    // otherwise the others keep sending and take their own 429s.
                    var pause = _pausedUntil - _utcNow();
                    if (pause > wait) wait = pause;
                }
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, ct);
                    lock (_lock) { Refill(); _tokens = Math.Max(_tokens, 1); }
                }
                lock (_lock) _tokens -= 1;
            }
            finally { _gate.Release(); }
        }

        private void Refill()
        {
            var now = _utcNow();
            _tokens = Math.Min(_capacity, _tokens + (now - _lastRefill).TotalSeconds * _refillPerSecond);
            _lastRefill = now;
        }

        public void NoteThrottled(TimeSpan pause)
        {
            lock (_lock)
            {
                _tokens = 0;
                _lastRefill = _utcNow();
                var until = _utcNow() + pause;
                if (until > _pausedUntil) _pausedUntil = until;
            }
            Log.Warning("Trakt {Bucket} bucket throttled; pausing that category for {Pause}", _name, pause);
        }

        public void Observe(int remaining)
        {
            lock (_lock)
            {
                Refill(); // credit elapsed time first, so the clamp isn't credited again later
                _tokens = Math.Min(_tokens, remaining);
            }
        }
    }
}
