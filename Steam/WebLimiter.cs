using System.Collections.Concurrent;

namespace TradeInventory.Steam;

/// <summary>ASF-like web rate limiter: one in-flight request per host + delay.</summary>
public static class WebLimiter
{
    /// <summary>ASF GlobalConfig.DefaultWebLimiterDelay is 300 ms.</summary>
    public static int DelayMs { get; set; } = 300;

    static readonly ConcurrentDictionary<string, SemaphoreSlim> HostGates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<T> LimitAsync<T>(string host, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(host)) host = "steam";
        var gate = HostGates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            if (DelayMs > 0)
            {
                try { await Task.Delay(DelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* ignore */ }
            }
            gate.Release();
        }
    }

    public static Task LimitAsync(string host, Func<CancellationToken, Task> action, CancellationToken ct = default) =>
        LimitAsync(host, async c => { await action(c).ConfigureAwait(false); return 0; }, ct);
}

/// <summary>DelegatingHandler that applies WebLimiter to every HTTP call (ASF WebLimitRequest).</summary>
public sealed class WebLimitHandler : DelegatingHandler
{
    public WebLimitHandler(HttpMessageHandler inner) : base(inner) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "steam";
        return WebLimiter.LimitAsync(host, ct => base.SendAsync(request, ct), cancellationToken);
    }
}
