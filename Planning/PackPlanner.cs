using TradeInventory.Models;

namespace TradeInventory.Planning;

public static class PackPlanner
{
    /// <summary>
    /// Fill each destination so existing_value + sent >= target.
    /// Prefer fewer source accounts (richest first).
    /// </summary>
    public static (List<DestPack> Packs, List<InventoryItem> Leftover, string StopReason) Plan(
        List<InventoryItem> pool,
        IReadOnlyDictionary<string, decimal> destExisting,
        IReadOnlyList<SteamAccount> destinations,
        decimal target)
    {
        var units = Expand(pool);
        var packs = new List<DestPack>();
        var stop = "";

        foreach (var dest in destinations)
        {
            var existing = destExisting.TryGetValue(dest.Username, out var e) ? e : 0m;
            var need = target - existing;
            if (need <= 0)
            {
                packs.Add(new DestPack
                {
                    DestUsername = dest.Username,
                    ExistingValue = existing,
                    Need = 0,
                });
                continue;
            }

            if (units.Sum(u => u.PriceUsd) < need)
            {
                stop = $"remaining pool ${units.Sum(u => u.PriceUsd):F2} < need ${need:F2} for {dest.Username}";
                break;
            }

            var bySrc = units.GroupBy(u => u.SourceUsername)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.PriceUsd).ThenBy(x => x.MarketHashName).ToList());
            var sources = bySrc.Keys.OrderByDescending(s => bySrc[s].Sum(i => i.PriceUsd)).ThenBy(s => s).ToList();

            var chosen = new List<InventoryItem>();
            var total = 0m;
            var usedFromSrc = new Dictionary<string, List<InventoryItem>>();

            foreach (var src in sources)
            {
                if (total >= need) break;
                foreach (var u in bySrc[src])
                {
                    if (total >= need) break;
                    chosen.Add(u);
                    if (!usedFromSrc.TryGetValue(src, out var list))
                        usedFromSrc[src] = list = new List<InventoryItem>();
                    list.Add(u);
                    total += u.PriceUsd;
                }
            }

            if (total < need)
            {
                stop = $"could not reach need ${need:F2} for {dest.Username} (best ${total:F2})";
                break;
            }

            // Drop trailing poorest sources if still >= need
            var order = usedFromSrc.Keys.ToList();
            while (order.Count > 1)
            {
                var drop = order[^1];
                var dropSum = usedFromSrc[drop].Sum(i => i.PriceUsd);
                if (total - dropSum >= need)
                {
                    total -= dropSum;
                    foreach (var i in usedFromSrc[drop]) chosen.Remove(i);
                    usedFromSrc.Remove(drop);
                    order.RemoveAt(order.Count - 1);
                }
                else break;
            }

            // Remove by reference so partial stack consumption keeps remaining units.
            var taken = chosen.ToHashSet();
            units = units.Where(u => !taken.Contains(u)).ToList();

            var pack = new DestPack
            {
                DestUsername = dest.Username,
                ExistingValue = existing,
                Need = need,
            };
            pack.Items.AddRange(chosen);
            packs.Add(pack);
        }

        return (packs, units, stop);
    }

    static List<InventoryItem> Expand(List<InventoryItem> pool)
    {
        var units = new List<InventoryItem>();
        foreach (var i in pool)
        {
            for (var n = 0; n < i.Amount; n++)
            {
                units.Add(new InventoryItem
                {
                    SourceUsername = i.SourceUsername,
                    AssetId = i.AssetId,
                    ClassId = i.ClassId,
                    InstanceId = i.InstanceId,
                    MarketHashName = i.MarketHashName,
                    Amount = 1,
                    PriceUsd = i.PriceUsd,
                    Tradable = i.Tradable,
                    Marketable = i.Marketable,
                });
            }
        }
        return units;
    }
}
