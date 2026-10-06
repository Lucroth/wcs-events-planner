using System.Net;
using System.Runtime.ExceptionServices;

namespace WcsEvents.Sync.Crawl;

/// <summary>
/// Every request to the registry and to scoring.dance passes through here, whoever makes it: it
/// waits its turn at the shared <see cref="RateGate"/>, gets a timeout of its own, and is retried
/// when the site is briefly unavailable — a dropped connection, a timeout, a 5xx, or a 429 whose
/// Retry-After is honoured. A site that keeps failing still fails the request, so an outage is never
/// mistaken for a page that does not exist.
/// </summary>
public sealed partial class PoliteHttpHandler(RateGate gate, ILogger<PoliteHttpHandler> logger) : DelegatingHandler
{
    internal const int MaxAttempts = 3;

    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest Retry-After waited out; a site asking for longer gets its answer passed on.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>Wait before the second attempt, doubled for each one after it, unless the site names one.</summary>
    internal TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await gate.WaitAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AttemptTimeout);

            HttpResponseMessage? response = null;
            Exception? failure = null;

            try
            {
                response = await base.SendAsync(request, timeout.Token);
                await response.Content.LoadIntoBufferAsync(timeout.Token);
            }
            catch (HttpRequestException ex)
            {
                failure = ex;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                failure = new TimeoutException($"{request.RequestUri} gave no answer within {AttemptTimeout.TotalSeconds:0}s.", ex);
            }

            if (failure is null && !IsTransient(response!.StatusCode))
            {
                return response;
            }

            var delay = DelayFor(response, attempt, BaseDelay);
            if (attempt >= MaxAttempts || delay > MaxRetryAfter)
            {
                if (failure is not null)
                {
                    response?.Dispose();
                    ExceptionDispatchInfo.Throw(failure);
                }

                return response!;
            }

            LogRetry(logger, request.RequestUri, attempt, (int?)response?.StatusCode, delay, failure);
            response?.Dispose();

            await Task.Delay(delay, ct);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>The site's own Retry-After when it sent one, otherwise an exponential backoff.</summary>
    internal static TimeSpan DelayFor(HttpResponseMessage? response, int attempt, TimeSpan baseDelay) =>
        response?.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is { Ticks: > 0 } left ? left : TimeSpan.Zero,
            _ => baseDelay * Math.Pow(2, attempt - 1),
        };

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Uri} failed on attempt {Attempt} (status {Status}); retrying in {Delay}")]
    private static partial void LogRetry(ILogger logger, Uri? uri, int attempt, int? status, TimeSpan delay, Exception? ex);
}
