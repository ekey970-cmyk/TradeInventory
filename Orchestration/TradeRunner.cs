using System.Collections.Concurrent;
using TradeInventory.Data;
using TradeInventory.Models;
using TradeInventory.Planning;
using TradeInventory.Proxy;
using TradeInventory.Steam;

namespace TradeInventory.Orchestration;

public sealed record TradeRunnerOptions
{
    public string Root { get; init; } = "";
    public string SourcesCsv { get; init; } = "prime_accounts.csv";
    public string DestCsv { get; init; } = "unlimited_5_dollars.csv";
    public string OutCsv { get; init; } = "unlimited_10_inv.csv";
    public string ProxiesFile { get; init; } = "proxies.txt";
    public string PriceCache { get; init; } = "price_cache.json";
    public string TokenCache { get; init; } = "trade_tokens.json";
    public string SessionsDir { get; init; } = "sessions";
    public decimal TargetUsd { get; init; } = 11.5m;
    public decimal MinPrice { get; init; } = 0.10m;
    public int ScanWorkers { get; init; } = 6;
    public int TradeWorkers { get; init; } = 1; // ASF-like: one pack at a time
    public int TokenWorkers { get; init; } = 3;
    public int Limit { get; init; } = 0;
    public int Offset { get; init; } = 0;
    public int SourceLimit { get; init; } = 0;
    public int SourceOffset { get; init; } = 0;
    public bool DryRun { get; init; }
    public bool TokensOnly { get; init; }
    public bool RefreshTokens { get; init; }
    public bool ForceLogin { get; init; }
}

public sealed class TradeRunner
{
    readonly TradeRunnerOptions _opt;
    readonly string _root;

    public TradeRunner(TradeRunnerOptions opt)
    {
        _opt = opt;
        _root = string.IsNullOrEmpty(opt.Root) ? Directory.GetCurrentDirectory() : opt.Root;
    }

    string P(string rel) => Path.IsPathRooted(rel) ? rel : Path.Combine(_root, rel);

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        WebLimiter.DelayMs = 300; // ASF default
        ConfirmationsLimiter.DelaySeconds = 10; // ASF default

        var sources = CsvAccounts.Load(P(_opt.SourcesCsv));
        if (_opt.SourceOffset > 0) sources = sources.Skip(_opt.SourceOffset).ToList();
        if (_opt.SourceLimit > 0) sources = sources.Take(_opt.SourceLimit).ToList();

        var destinations = CsvAccounts.Load(P(_opt.DestCsv));
        if (_opt.Offset > 0) destinations = destinations.Skip(_opt.Offset).ToList();
        if (_opt.Limit > 0) destinations = destinations.Take(_opt.Limit).ToList();

        var proxies = new ProxyPool(ProxyPool.LoadFile(P(_opt.ProxiesFile)));
        var tokens = TokenCache.Load(P(_opt.TokenCache));
        foreach (var d in destinations)
        {
            var tok = tokens.Resolve(d);
            if (!string.IsNullOrEmpty(tok)) d.TradeToken = tok;
        }

        var sessionsDir = P(_opt.SessionsDir);
        await using var farm = new BotFarm(proxies, sessionsDir, _opt.ForceLogin, keepCm: true);

        Log.Info(
            $"Sources={sources.Count} | Dests={destinations.Count} | Proxies={proxies.Count} | " +
            $"Target=${_opt.TargetUsd:F2} | scan={_opt.ScanWorkers} | trade={_opt.TradeWorkers} | dry={_opt.DryRun} | ASF-bots=on");

        if (_opt.TokensOnly)
        {
            await PrefetchTokensAsync(farm, destinations, tokens, ct).ConfigureAwait(false);
            var all = CsvAccounts.Load(P(_opt.DestCsv));
            foreach (var d in destinations)
            {
                var row = all.FirstOrDefault(x => x.Username == d.Username);
                if (row is not null) row.TradeToken = d.TradeToken;
            }
            CsvAccounts.SaveDestinations(P(_opt.DestCsv), all);
            tokens.Save();
            Log.Info("Tokens saved.");
            return 0;
        }

        var prices = PriceCache.Load(P(_opt.PriceCache));
        Log.Info($"Prices: {prices.Count}");

        Log.Info("Scanning destination inventories (existing value)...");
        var destExisting = await ScanDestValuesAsync(farm, destinations, prices, ct).ConfigureAwait(false);

        Log.Info("Scanning prime inventories...");
        var pool = await ScanSourcesAsync(farm, sources, prices, ct).ConfigureAwait(false);
        var poolVal = pool.Sum(i => i.PriceUsd * i.Amount);
        Log.Info($"Pool: {pool.Sum(i => i.Amount)} pcs | ${poolVal:F2}");

        var (packs, leftover, stop) = PackPlanner.Plan(pool, destExisting, destinations, _opt.TargetUsd);
        var actionable = packs.Where(p => p.Need > 0 && p.Items.Count > 0).ToList();
        Log.Info($"Planned packs: {actionable.Count} (skipped already-full: {packs.Count(p => p.Need <= 0)})");
        foreach (var p in actionable)
            Log.Info(
                $"  -> {p.DestUsername}: have ${p.ExistingValue:F2} need ${p.Need:F2} send ${p.SentValue:F2} " +
                $"({p.Items.Count} pcs, sources={p.BySource().Count})");
        Log.Info($"Leftover: {leftover.Count} pcs | ${leftover.Sum(u => u.PriceUsd):F2}");
        if (!string.IsNullOrEmpty(stop)) Log.Info($"Stop preview: {stop}");
        if (actionable.Count == 0) { Log.Info("Nothing to send."); return 1; }

        if (!_opt.DryRun)
        {
            Log.Info("Prefetching trade tokens for planned destinations...");
            var packDests = actionable.Select(p => destinations.First(d => d.Username == p.DestUsername)).ToList();
            await PrefetchTokensAsync(farm, packDests, tokens, ct).ConfigureAwait(false);
            var allDest = CsvAccounts.Load(P(_opt.DestCsv));
            foreach (var d in packDests)
            {
                var row = allDest.FirstOrDefault(x => x.Username == d.Username);
                if (row is not null) row.TradeToken = d.TradeToken;
            }
            CsvAccounts.SaveDestinations(P(_opt.DestCsv), allDest);
            tokens.Save();
        }

        var workers = Math.Max(1, Math.Min(_opt.TradeWorkers, actionable.Count));
        Log.Info($"Trade workers: {workers} | ASF mode (sticky bots, WebLimiter={WebLimiter.DelayMs}ms, CM kept, confirm=McKay/steamcommunity)");

        var ok = 0;
        var failed = new ConcurrentBag<(string Name, string Err)>();
        await Parallel.ForEachAsync(
            actionable.Select((p, i) => (p, i)),
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            async (item, token) =>
            {
                var (pack, idx) = item;
                var dest = destinations.First(d => d.Username == pack.DestUsername);
                Log.Info($"[{idx + 1}/{actionable.Count}] Filling {dest.Username} ...");
                try
                {
                    var date = await ExecutePackAsync(farm, pack, sources, dest, token).ConfigureAwait(false);
                    if (!_opt.DryRun)
                    {
                        lock (typeof(TradeRunner))
                            CsvAccounts.AppendSuccess(P(_opt.DestCsv), P(_opt.OutCsv), dest, date);
                        Log.Info($"  [{dest.Username}] OK trade_date={date}");
                    }
                    else Log.Info($"  [{dest.Username}] DRY ok");
                    Interlocked.Increment(ref ok);
                }
                catch (Exception ex)
                {
                    failed.Add((dest.Username, ex.Message));
                    Log.Info($"  [{dest.Username}] ERROR: {ex.Message}");
                }
            }).ConfigureAwait(false);

        Log.Info($"Done: ok={ok}/{actionable.Count} failed={failed.Count}");
        foreach (var (n, e) in failed.Take(10))
            Log.Info($"  - {n}: {e}");
        return ok > 0 ? 0 : 1;
    }

    async Task PrefetchTokensAsync(BotFarm farm, List<SteamAccount> accounts, TokenCache tokens, CancellationToken ct)
    {
        var missing = new List<SteamAccount>();
        foreach (var a in accounts)
        {
            if (!_opt.RefreshTokens)
            {
                var tok = tokens.Resolve(a);
                if (!string.IsNullOrEmpty(tok)) { a.TradeToken = tok; continue; }
            }
            missing.Add(a);
        }
        Log.Info($"Trade tokens: cached={accounts.Count - missing.Count} missing={missing.Count}");
        if (missing.Count == 0) return;

        var workers = Math.Max(1, Math.Min(_opt.TokenWorkers, missing.Count));
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            async (acc, token) =>
            {
                var tok = await farm.RunAsync(acc, "trade token",
                    s => TradeClient.FetchTradeTokenAsync(s, token),
                    maxAttempts: 8, ct: token).ConfigureAwait(false);
                tokens.Put(acc, tok);
                Log.Info($"  [token] {acc.Username}: ok");
            }).ConfigureAwait(false);
        tokens.Save();
    }

    async Task<Dictionary<string, decimal>> ScanDestValuesAsync(
        BotFarm farm, List<SteamAccount> destinations, PriceCache prices, CancellationToken ct)
    {
        var map = new ConcurrentDictionary<string, decimal>(StringComparer.Ordinal);
        var workers = Math.Max(1, Math.Min(_opt.ScanWorkers, destinations.Count));
        await Parallel.ForEachAsync(destinations, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            async (acc, token) =>
            {
                try
                {
                    var items = await farm.RunAsync(acc, $"dest-inv {acc.Username}",
                        s => InventoryClient.FetchAsync(s, prices, 0, tradableOnly: false, marketableOnly: false, token),
                        maxAttempts: 8, ct: token).ConfigureAwait(false);
                    var val = InventoryClient.SumValue(items);
                    map[acc.Username] = val;
                    Log.Info($"  [dest] {acc.Username}: ${val:F2} ({items.Sum(i => i.Amount)} pcs)");
                }
                catch (Exception ex)
                {
                    map[acc.Username] = 0;
                    Log.Info($"  [dest] {acc.Username}: ERROR {ex.Message} (assume $0)");
                }
            }).ConfigureAwait(false);
        return new Dictionary<string, decimal>(map);
    }

    async Task<List<InventoryItem>> ScanSourcesAsync(
        BotFarm farm, List<SteamAccount> sources, PriceCache prices, CancellationToken ct)
    {
        var bag = new ConcurrentBag<InventoryItem>();
        var workers = Math.Max(1, Math.Min(_opt.ScanWorkers, sources.Count));
        await Parallel.ForEachAsync(sources,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            async (acc, token) =>
            {
                try
                {
                    var items = await farm.RunAsync(acc, $"src-inv {acc.Username}",
                        s => InventoryClient.FetchAsync(s, prices, _opt.MinPrice, tradableOnly: true, marketableOnly: true, token),
                        maxAttempts: 8, ct: token).ConfigureAwait(false);
                    foreach (var it in items) bag.Add(it);
                    Log.Info($"  [source] {acc.Username}: {items.Sum(i => i.Amount)} pcs ${InventoryClient.SumValue(items):F2}");
                }
                catch (Exception ex)
                {
                    Log.Info($"  [source] {acc.Username}: ERROR {ex.Message}");
                }
            }).ConfigureAwait(false);
        return bag.ToList();
    }

    async Task<string> ExecutePackAsync(
        BotFarm farm,
        DestPack pack,
        List<SteamAccount> sources,
        SteamAccount dest,
        CancellationToken ct)
    {
        Log.Info(
            $"  pack {dest.Username}: have ${pack.ExistingValue:F2} + send ${pack.SentValue:F2} " +
            $"=> ${pack.ProjectedTotal:F2} from {pack.BySource().Count} source(s)");

        if (_opt.DryRun) return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var token = TokenCache.Normalize(dest.TradeToken);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException($"missing trade_token for {dest.Username}");

        // ASF: one trade pipeline globally (send+confirm), dest bot stays warm for accepts.
        await BotFarm.TradePipeline.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var destSession = await farm.GetOnlineAsync(dest, ct: ct).ConfigureAwait(false);

            var bySource = pack.BySource();
            var order = bySource.Keys
                .OrderByDescending(s => bySource[s].Sum(i => i.PriceUsd * i.Amount))
                .ToList();

            for (var i = 0; i < order.Count; i++)
            {
                var srcName = order[i];
                var items = bySource[srcName];
                var src = sources.First(s => s.Username == srcName);
                var sub = items.Sum(x => x.PriceUsd * x.Amount);
                Log.Info($"    offer {i + 1}/{order.Count}: {srcName} -> {dest.Username} ({items.Sum(x => x.Amount)} pcs ${sub:F2})");

                string? sentOfferId = null;
                var offerId = await farm.RunAsync(src, $"trade {srcName}->{dest.Username}",
                    async session =>
                    {
                        // If we already sent in a previous attempt of THIS call, only confirm.
                        if (sentOfferId is not null)
                        {
                            await MobileConfirmations.ConfirmTradeOfferAsync(
                                session, src.IdentitySecret, src.Steam64Id, sentOfferId,
                                maxWait: TimeSpan.FromSeconds(180), ct: ct).ConfigureAwait(false);
                            return sentOfferId;
                        }

                        var n = await TradeClient.CancelActiveSentOffersAsync(session, ct).ConfigureAwait(false);
                        if (n > 0)
                            Log.Info($"      cancelled {n} old sent offer(s) on {srcName}");
                        await Task.Delay(1000, ct).ConfigureAwait(false);

                        var (id, _) = await TradeClient.SendOfferAsync(
                            session, dest.Steam64Id, token, items, ct: ct).ConfigureAwait(false);
                        sentOfferId = id;
                        Log.Info($"      sent offer {id}");

                        await MobileConfirmations.ConfirmTradeOfferAsync(
                            session, src.IdentitySecret, src.Steam64Id, id,
                            maxWait: TimeSpan.FromSeconds(180), ct: ct).ConfigureAwait(false);
                        Log.Info($"      confirm offer {id}: ok");
                        return id;
                    },
                    maxAttempts: 5,
                    allowResendOnFail: true, // safe: lambda only re-confirms if sentOfferId set
                    ct: ct).ConfigureAwait(false);

                await Task.Delay(1000, ct).ConfigureAwait(false);
                await AcceptWithRetryAsync(destSession, dest, offerId, src.Steam64Id, ct).ConfigureAwait(false);
                Log.Info($"      accepted offer {offerId}");

                if (i + 1 < order.Count)
                    await Task.Delay(1500, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            BotFarm.TradePipeline.Release();
        }

        return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    static async Task AcceptWithRetryAsync(
        SteamWebSession destSession,
        SteamAccount dest,
        string offerId,
        string partnerSteam64,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await TradeClient.AcceptOfferAsync(
                    destSession, offerId, partnerSteam64,
                    destIdentitySecret: dest.IdentitySecret,
                    destSteam64: dest.Steam64Id,
                    ct: ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                if (!Proxy.ProxyErrors.IsProxyOrRate(ex) || attempt >= 5)
                    throw;
                Log.Info($"      accept retry {attempt}/5: {ex.Message}");
                await Task.Delay(1500 * attempt, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException("accept failed");
    }
}
