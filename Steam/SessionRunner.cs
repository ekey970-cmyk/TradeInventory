using TradeInventory.Models;
using TradeInventory.Proxy;

namespace TradeInventory.Steam;

/// <summary>Compatibility wrapper — prefer <see cref="BotFarm"/> (ASF-style long-lived bots).</summary>
public static class SessionRunner
{
    public static SemaphoreSlim AccountLock(string username) =>
        throw new InvalidOperationException("Use BotFarm — AccountLock is obsolete.");

    public static Task<T> RunAsync<T>(
        SteamAccount account,
        ProxyPool proxies,
        string sessionsDir,
        string opName,
        Func<SteamWebSession, Task<T>> action,
        int maxAttempts = 8,
        bool probe = true,
        bool forceLogin = false,
        CancellationToken ct = default) =>
        throw new InvalidOperationException("Use BotFarm.RunAsync instead of SessionRunner.");
}
