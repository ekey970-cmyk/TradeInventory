namespace TradeInventory.Steam;

/// <summary>ASF ConfirmationsSemaphore + ConfirmationsLimiterDelay (default 10 seconds).</summary>
public static class ConfirmationsLimiter
{
    /// <summary>ASF GlobalConfig.DefaultConfirmationsLimiterDelay</summary>
    public static int DelaySeconds { get; set; } = 10;

    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task WaitAsync(CancellationToken ct = default)
    {
        if (DelaySeconds <= 0) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(DelaySeconds)).ConfigureAwait(false); }
            finally { Gate.Release(); }
        }, CancellationToken.None);
    }
}
