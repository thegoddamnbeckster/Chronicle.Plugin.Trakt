using System.Net;
using Xunit;

namespace Chronicle.Plugin.Trakt.Tests;

/// <summary>The checklist's K7: a call that fails on purpose must not carry the credentials out in its error text.</summary>
public class SecretProofTests
{
    private const string Id = "CLIENT-ID-SECRET-1234567890";
    private const string Secret = "CLIENT-SECRET-0987654321";

    private sealed class Handler(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(code));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_failing_call_leaves_both_credentials_out_of_whatever_it_throws(HttpStatusCode code)
    {
        var http = new HttpClient(new Handler(code)) { BaseAddress = new Uri("https://api.trakt.tv") };
        var client = new TraktClient(Id, Secret, http);
        var ex = await Record.ExceptionAsync(() => client.SearchAsync("movie", "anything", null, default));
        if (ex is not null)
        {
            Assert.DoesNotContain(Id, ex.ToString());
            Assert.DoesNotContain(Secret, ex.ToString());
        }
    }

    [Fact]
    public async Task A_rate_limit_that_does_not_clear_after_the_one_wait_is_a_pause_that_says_how_long_Trakt_asked()
    {
        var calls = 0;
        var http = new HttpClient(new Limited(() => calls++)) { BaseAddress = new Uri("https://api.trakt.tv") };
        var client = new TraktClient(Id, Secret, http, new TraktRateLimiter());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMovieAsync("tron", default));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);              // a pause for Chronicle's queue, not "not found"
        Assert.Equal(TimeSpan.FromMilliseconds(1), ex.Data["RetryAfter"]);
        Assert.Equal(2, calls);                                                    // one wait, one retry, then it says so
    }

    private sealed class Limited(Action count) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count();
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return Task.FromResult(r);
        }
    }
}
