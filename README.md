# TradeInventory (C#)

CS2 inventory redistribution: move items from `prime_accounts.csv` onto
`unlimited_5_dollars.csv` so each destination ends at **$11.50** (including
items already on the dest).

Login uses **SteamKit2** (same stack as ASF). Trade / inventory / mobileconf
follow **ArchiSteamFarm** web protocols; `steamLoginSecure` is set exactly like
`ArchiWebHandler.Init` (`{steamid}||{accessToken}`).

Trade / inventory / mobile confirmation flows follow **ArchiSteamFarm** web
protocols (`m=react`, confirmation HMAC always with `tag=conf`). ASF itself is
not a NuGet library — this tool reimplements the same HTTP endpoints.

## Build

```bash
export DOTNET_CLI_HOME=../.dotnet-cli
export NUGET_PACKAGES=../.nuget/packages
dotnet build -c Release
```

## Run

From `TradeInventory/` (or pass `--root` to the project parent):

```bash
dotnet run -c Release -- --root .. --dry-run --limit 1 --source-limit 3
dotnet run -c Release -- --root .. --tokens-only
dotnet run -c Release -- --root .. --trade-workers 10
```

### Flags

| Flag | Default | Meaning |
|------|---------|---------|
| `--target` | `11.5` | Desired dest inventory USD (existing + sent) |
| `--min-price` | `0.10` | Skip cheaper source items |
| `--workers` | `10` | Inventory scan parallelism |
| `--trade-workers` | `10` | Parallel destination packs |
| `--dry-run` | | Scan + plan only |
| `--tokens-only` | | Prefetch/cache trade tokens |
| `--force-login` | | Ignore saved `sessions/` cookies |

## Behaviour

1. Scan dest inventories → existing USD
2. Scan prime inventories → priced tradable pool
3. Plan packs: `need = target − existing`, prefer fewer/richer sources
4. Prefetch dest `trade_token` (CSV + `trade_tokens.json`)
5. Per pack: acknowledge Trade Protection → send offer → mobile confirm (`tag=conf`, same as `trade_item.py` / `steam_trade_ops.py`) → dest accept (+ dest confirm if needed)
6. Proxies: probe before use; blacklist/rotate on 429/5xx/timeouts
7. Confirm failure does **not** re-send (avoids duplicate offers)
