using System.Collections.Concurrent;
using TradeInventory.Models;
using TradeInventory.Proxy;

namespace TradeInventory.Steam;

/// <summary>
/// ASF-like bot pool: one long-lived web+SteamKit session per account, sticky proxy,
/// recreate only on hard failures. Trades share a global gate (one confirm/send pipeline).
/// </summary>
public sealed class BotFarm : IAsyncDisposable
{
    readonly ProxyPool _proxies;
    readonly string _sessionsDir;
    readonly bool _forceLogin;
    readonly bool _keepCm;
    readonly ConcurrentDictionary<string, BotSlot> _bots = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, SemaphoreSlim> _accountLocks = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _loginSlots = new(2, 2);

    /// <summary>Global trade pipeline like a single ASF process doing one loot at a time.</summary>
    public static SemaphoreSlim TradePipeline { get; } = new(1, 1);

    public BotFarm(ProxyPool proxies, string sessionsDir, bool forceLogin = false, bool keepCm = true)
    {
        _proxies = proxies;
        _sessionsDir = sessionsDir;
        _forceLogin = forceLogin;
        _keepCm = keepCm;
    }

    public async Task<T> RunAsync<T>(
        SteamAccount account,
        string opName,
        Func<SteamWebSession, Task<T>> action,
        int maxAttempts = 6,
        bool allowResendOnFail = true,
        CancellationToken ct = default)
    {
        var gate = _accountLocks.GetOrAdd(account.Username, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var session = await GetOnlineAsync(account, forceRelogin: attempt > 1 && last is not null, ct)
                        .ConfigureAwait(false);
                    return await action(session).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    last = ex;
                    var shortMsg = ex.Message.Length > 160 ? ex.Message[..157] + "..." : ex.Message;

                    if (!allowResendOnFail &&
                        (ex.Message.Contains("mobile confirmation", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("confirm timeout", StringComparison.OrdinalIgnoreCase) ||
                         ex.Message.Contains("offer already sent", StringComparison.OrdinalIgnoreCase)))
                    {
                        Log.Info($"      {opName} confirm failed (no resend): {shortMsg}");
                        break;
                    }

                    var badProxy = ProxyErrors.IsProxyOrRate(ex);
                    var needRelogin = ex.Message.Contains("needauth", StringComparison.OrdinalIgnoreCase)
                                      || ex.Message.Contains("LogOn", StringComparison.OrdinalIgnoreCase)
                                      || ex.Message.Contains("BeginAuth", StringComparison.OrdinalIgnoreCase);

                    Log.Info(
                        $"      {opName} error ({shortMsg}); " +
                        $"{(badProxy ? "rotate proxy" : needRelogin ? "relogin" : "retry")} " +
                        $"({attempt}/{maxAttempts})");

                    await DropBotAsync(account.Username, blacklistProxy: badProxy).ConfigureAwait(false);

                    if (attempt >= maxAttempts) break;
                    var delay = ex.Message.Contains("429", StringComparison.Ordinal)
                        ? 10000 + attempt * 5000
                        : badProxy ? 800 : 1500 * attempt;
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException($"{opName} failed after retries: {last?.Message}", last);
        }
        finally { gate.Release(); }
    }

    public async Task<SteamWebSession> GetOnlineAsync(SteamAccount account, bool forceRelogin = false, CancellationToken ct = default)
    {
        if (!forceRelogin && _bots.TryGetValue(account.Username, out var existing) && existing.Session is not null)
        {
            try
            {
                if (await existing.Session.SessionLooksValidPublicAsync(ct).ConfigureAwait(false))
                    return existing.Session;
            }
            catch { /* recreate */ }
            await DropBotAsync(account.Username, blacklistProxy: false).ConfigureAwait(false);
        }

        await _loginSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check after waiting for login slot
            if (!forceRelogin && _bots.TryGetValue(account.Username, out var raced) && raced.Session is not null)
                return raced.Session;

            var (idx, proxy) = _proxies.Acquire();
            SteamWebSession? session = null;
            try
            {
                session = new SteamWebSession(account, proxy, _sessionsDir, keepCmConnected: _keepCm);
                await session.ProbeAsync(ct).ConfigureAwait(false);
                await session.EnsureLoggedInAsync(_forceLogin || forceRelogin, ct).ConfigureAwait(false);
                var slot = new BotSlot(session, idx);
                _bots[account.Username] = slot;
                Log.Info($"      [bot] {account.Username} online via {_proxies.Tag(proxy)} (cm={(session.IsCmLoggedOn ? "up" : "web-only")})");
                return session;
            }
            catch
            {
                session?.Dispose();
                _proxies.Release(idx, bad: true);
                throw;
            }
        }
        finally { _loginSlots.Release(); }
    }

    public async Task DropBotAsync(string username, bool blacklistProxy)
    {
        if (!_bots.TryRemove(username, out var slot)) return;
        try { slot.Session?.Dispose(); } catch { /* ignore */ }
        if (slot.ProxyIdx >= 0)
            _proxies.Release(slot.ProxyIdx, blacklistProxy);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var user in _bots.Keys.ToList())
            await DropBotAsync(user, blacklistProxy: false).ConfigureAwait(false);
    }

    sealed class BotSlot(SteamWebSession session, int proxyIdx)
    {
        public SteamWebSession Session { get; } = session;
        public int ProxyIdx { get; } = proxyIdx;
    }
}
