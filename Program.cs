using TradeInventory.Orchestration;

// Minimal CLI without System.CommandLine package (NuGet unavailable in this env).
static class Program
{
    static async Task<int> Main(string[] args)
    {
        var opt = Parse(args);
        // default root = parent of bin or cwd that contains CSVs
        if (string.IsNullOrEmpty(opt.Root))
        {
            var cwd = Directory.GetCurrentDirectory();
            if (File.Exists(Path.Combine(cwd, "prime_accounts.csv")))
                opt = opt with { Root = cwd };
            else if (File.Exists(Path.Combine(cwd, "..", "prime_accounts.csv")))
                opt = opt with { Root = Path.GetFullPath(Path.Combine(cwd, "..")) };
            else
                opt = opt with { Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")) };
        }

        Log.Info($"TradeInventory (SteamKit2 + ASF web) root={opt.Root}");
        var runner = new TradeRunner(opt);
        try
        {
            return await runner.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            return 2;
        }
    }

    static TradeRunnerOptions Parse(string[] args)
    {
        var o = new TradeRunnerOptions();
        string? root = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
            switch (args[i])
            {
                case "--root": root = Next(); break;
                case "--sources": o = o with { SourcesCsv = Next() }; break;
                case "--destinations": o = o with { DestCsv = Next() }; break;
                case "--out": o = o with { OutCsv = Next() }; break;
                case "--proxies": o = o with { ProxiesFile = Next() }; break;
                case "--target": o = o with { TargetUsd = decimal.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture) }; break;
                case "--min-price": o = o with { MinPrice = decimal.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture) }; break;
                case "--workers": o = o with { ScanWorkers = int.Parse(Next()) }; break;
                case "--trade-workers": o = o with { TradeWorkers = int.Parse(Next()) }; break;
                case "--token-workers": o = o with { TokenWorkers = int.Parse(Next()) }; break;
                case "--limit": o = o with { Limit = int.Parse(Next()) }; break;
                case "--offset": o = o with { Offset = int.Parse(Next()) }; break;
                case "--source-limit": o = o with { SourceLimit = int.Parse(Next()) }; break;
                case "--source-offset": o = o with { SourceOffset = int.Parse(Next()) }; break;
                case "--dry-run": o = o with { DryRun = true }; break;
                case "--tokens-only": o = o with { TokensOnly = true }; break;
                case "--refresh-tokens": o = o with { RefreshTokens = true }; break;
                case "--force-login": o = o with { ForceLogin = true }; break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown arg: {args[i]}");
            }
        }
        if (root is not null) o = o with { Root = root };
        return o;
    }

    static void PrintHelp()
    {
        Log.Info("""
TradeInventory — CS2 inventory redistribution (SteamKit2 bots + ASF web)

  --root PATH           Project root with CSVs (auto-detected)
  --sources FILE        prime_accounts.csv
  --destinations FILE   unlimited_5_dollars.csv
  --out FILE            unlimited_10_inv.csv
  --proxies FILE        proxies.txt
  --target N            Target inventory USD (default 11.5) including existing items
  --min-price N         Min item price (default 0.10)
  --workers N           Inventory scan parallelism (default 10)
  --trade-workers N     Parallel destination packs (default 10)
  --token-workers N     Token prefetch parallelism (default 5)
  --limit N / --offset N
  --dry-run             Plan only
  --tokens-only         Prefetch trade tokens and exit
  --refresh-tokens      Ignore cached tokens
  --force-login         Ignore saved sessions
""");
    }
}
