using System.Threading.RateLimiting;

namespace WcsEvents.Sync.Crawl;

/// <summary>Shared politeness budget for every outbound crawl request.</summary>
public sealed class RateGate : IDisposable
{
    /// <summary>The shortest replenishment period; below this a timer tick is coarser than the period.</summary>
    private const double MinPeriodSeconds = 0.05;

    private readonly TokenBucketRateLimiter limiter;

    public RateGate(CrawlOptions options)
    {
        limiter = new TokenBucketRateLimiter(OptionsFor(options.RequestsPerSecond));
    }

    /// <summary>
    /// A bucket that refills at exactly <paramref name="perSecond"/>, fractional rates included: whole
    /// tokens arrive every <c>tokens / rate</c> seconds, with the period kept long enough for the
    /// replenishment timer to honour it.
    /// </summary>
    internal static TokenBucketRateLimiterOptions OptionsFor(double perSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);

        var tokensPerPeriod = Math.Max(1, (int)Math.Ceiling(perSecond * MinPeriodSeconds));

        return new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(tokensPerPeriod, (int)Math.Ceiling(perSecond)),
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(tokensPerPeriod / perSecond),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        };
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        using var lease = await limiter.AcquireAsync(1, ct);
    }

    public void Dispose() => limiter.Dispose();
}
