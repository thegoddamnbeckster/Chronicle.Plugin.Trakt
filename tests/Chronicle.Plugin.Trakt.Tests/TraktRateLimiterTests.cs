namespace Chronicle.Plugin.Trakt.Tests;

/// <summary>
/// Trakt's published limits (docs.trakt.tv, "Rate Limiting"): AUTHED_API_GET_LIMIT /
/// UNAUTHED_API_GET_LIMIT = 1,000 GET calls per 5-minute rolling window,
/// AUTHED_API_POST_LIMIT = 1 POST/PUT/DELETE call per second. Unlike Hardcover there is no daily
/// quota to exhaust, so the limiter is just two independent token buckets -- GET and write -- each
/// kept under the published rate with margin.
/// </summary>
public class TraktRateLimiterTests
{
    /// <summary>A limiter on a fake clock whose Delay just advances that clock.</summary>
    internal sealed class Fake
    {
        public DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        public TimeSpan Waited = TimeSpan.Zero;
        public TraktRateLimiter Limiter { get; }
        public Fake() => Limiter = new TraktRateLimiter(() => Now, (t, _) => { Now += t; Waited += t; return Task.CompletedTask; });
    }

    // ── GET bucket ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ABurstUpToGetBucketCapacity_IsNotDelayed()
    {
        var f = new Fake();
        for (var i = 0; i < (int)TraktRateLimiter.GetBucketCapacity; i++)
            await f.Limiter.AcquireAsync(HttpMethod.Get, default);

        Assert.Equal(TimeSpan.Zero, f.Waited);
    }

    [Fact]
    public async Task InAnySlidingFiveMinuteWindow_StaysUnderTraktsThousandCallLimit()
    {
        var f = new Fake();
        var sent = new List<DateTime>();
        for (var i = 0; i < 2000; i++)
        {
            await f.Limiter.AcquireAsync(HttpMethod.Get, default);
            sent.Add(f.Now);
        }

        for (var i = 0; i < sent.Count; i++)
        {
            var window = sent.Skip(i).TakeWhile(t => t - sent[i] <= TimeSpan.FromMinutes(5)).Count();
            Assert.True(window < 1000, $"{window} GET calls inside a 5-minute window starting at #{i}");
        }

        // It is a real limiter, not a stall: long-run throughput is close to (but under) 1000/5min.
        var perFiveMin = (sent.Count - TraktRateLimiter.GetBucketCapacity) / ((sent[^1] - sent[0]).TotalMinutes / 5.0);
        Assert.InRange(perFiveMin, 850, 950);
    }

    [Fact]
    public async Task AFullGetRefill_AllowsAnotherFullBurst_ButNotAnExtraRequestOnTopOfIt()
    {
        var f = new Fake();
        var capacity = (int)TraktRateLimiter.GetBucketCapacity;
        for (var i = 0; i < capacity; i++) await f.Limiter.AcquireAsync(HttpMethod.Get, default);
        f.Now += TimeSpan.FromHours(1);

        for (var i = 0; i < capacity; i++) await f.Limiter.AcquireAsync(HttpMethod.Get, default);
        Assert.Equal(TimeSpan.Zero, f.Waited);

        await f.Limiter.AcquireAsync(HttpMethod.Get, default);
        Assert.True(f.Waited > TimeSpan.Zero);
    }

    // ── Write bucket (POST/PUT/DELETE) ──────────────────────────────────────

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task WriteCalls_AreNeverPacedFasterThanOnePerSecond(string methodName)
    {
        var method = new HttpMethod(methodName);
        var f = new Fake();
        var sent = new List<DateTime>();
        for (var i = 0; i < 20; i++)
        {
            await f.Limiter.AcquireAsync(method, default);
            sent.Add(f.Now);
        }

        for (var i = 1; i < sent.Count; i++)
            Assert.True(sent[i] - sent[i - 1] >= TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(1),
                $"write call #{i} followed #{i - 1} by only {(sent[i] - sent[i - 1]).TotalMilliseconds}ms");

        // Real pacing, not an indefinite stall: 20 calls complete in well under a minute.
        Assert.True(sent[^1] - sent[0] < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task GetAndWriteBuckets_AreIndependent()
    {
        var f = new Fake();
        for (var i = 0; i < (int)TraktRateLimiter.GetBucketCapacity; i++)
            await f.Limiter.AcquireAsync(HttpMethod.Get, default); // drains the GET bucket only

        Assert.Equal(TimeSpan.Zero, f.Waited);

        await f.Limiter.AcquireAsync(HttpMethod.Post, default); // write bucket starts full regardless
        Assert.Equal(TimeSpan.Zero, f.Waited);
    }

    // ── 429 handling ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A429OnGet_PausesOtherGetCallers_ButLeavesWriteCallersUnaffected()
    {
        var f = new Fake();
        f.Limiter.NoteThrottled(HttpMethod.Get, TimeSpan.FromSeconds(31));

        await f.Limiter.AcquireAsync(HttpMethod.Post, default); // a different caller, different category
        Assert.Equal(TimeSpan.Zero, f.Waited);

        await f.Limiter.AcquireAsync(HttpMethod.Get, default); // a different GET caller
        Assert.True(f.Waited >= TimeSpan.FromSeconds(31));
    }

    // ── X-Ratelimit header ───────────────────────────────────────────────────

    [Fact]
    public void ObserveClampsTheMatchingBucketDown_ButNeverUp()
    {
        var f = new Fake();
        f.Limiter.Observe(HttpMethod.Get, ["""{"name":"AUTHED_API_GET_LIMIT","period":300,"limit":1000,"remaining":2}"""]);
        Assert.Equal(2, f.Limiter.GetTokens);

        f.Limiter.Observe(HttpMethod.Get, ["""{"name":"AUTHED_API_GET_LIMIT","period":300,"limit":1000,"remaining":29}"""]);
        Assert.Equal(2, f.Limiter.GetTokens); // 29 does not raise what we already believe is left
    }

    [Fact]
    public void ObserveOnlyAffectsTheBucketMatchingTheRequestMethod()
    {
        var f = new Fake();
        f.Limiter.Observe(HttpMethod.Post, ["""{"name":"AUTHED_API_POST_LIMIT","period":1,"limit":1,"remaining":0}"""]);

        Assert.Equal(TraktRateLimiter.GetBucketCapacity, f.Limiter.GetTokens); // untouched
        Assert.Equal(0, f.Limiter.WriteTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("""{"remaining":"not-a-number"}""")]
    public void AMalformedOrMissingHeader_IsIgnored_AndNeverThrows(string header)
    {
        var f = new Fake();

        f.Limiter.Observe(HttpMethod.Get, [header]);

        Assert.Equal(TraktRateLimiter.GetBucketCapacity, f.Limiter.GetTokens);
    }
}
