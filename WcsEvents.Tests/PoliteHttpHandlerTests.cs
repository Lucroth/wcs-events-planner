using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using WcsEvents.Sync.Crawl;

namespace WcsEvents.Tests;

public class PoliteHttpHandlerTests
{
    /// <summary>Answers each request with the next status in line, counting the requests.</summary>
    private sealed class Scripted(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(statuses[Calls++]) { Content = new StringContent("{}") };
            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            }

            return Task.FromResult(response);
        }
    }

    private static HttpClient Client(Scripted inner) =>
        new(new PoliteHttpHandler(new RateGate(new CrawlOptions { RequestsPerSecond = 1000 }), NullLogger<PoliteHttpHandler>.Instance)
        {
            BaseDelay = TimeSpan.Zero,
            InnerHandler = inner,
        })
        { BaseAddress = new Uri("https://example.test/") };

    [Fact]
    public async Task RetriesThrottlingAndServerErrorsUntilTheSiteAnswers()
    {
        var inner = new Scripted(HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        using var response = await Client(inner).GetAsync("page", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task PassesOnAnswersThatRetryingWouldNotChange()
    {
        var notFound = new Scripted(HttpStatusCode.NotFound);
        using var missing = await Client(notFound).GetAsync("page", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(1, notFound.Calls);

        var down = new Scripted(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var failed = await Client(down).GetAsync("page", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal(PoliteHttpHandler.MaxAttempts, down.Calls);
    }

    [Fact]
    public void WaitsAsLongAsTheSiteAsksOtherwiseBacksOff()
    {
        using var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), PoliteHttpHandler.DelayFor(throttled, attempt: 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(4), PoliteHttpHandler.DelayFor(null, attempt: 2, TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(6)]
    [InlineData(30)]
    [InlineData(1000)]
    public void RateGateRefillsAtExactlyTheConfiguredRate(double perSecond)
    {
        var options = RateGate.OptionsFor(perSecond);

        Assert.Equal(perSecond, options.TokensPerPeriod / options.ReplenishmentPeriod.TotalSeconds, precision: 3);
        Assert.True(options.ReplenishmentPeriod >= TimeSpan.FromMilliseconds(50));
        Assert.True(options.TokenLimit >= 1);
    }
}
