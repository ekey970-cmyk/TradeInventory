using System.Text.Json;
using TradeInventory.Data;
using TradeInventory.Models;

namespace TradeInventory.Steam;

public static class InventoryClient
{
    const int AppId = 730;
    const string ContextId = "2";

    public static async Task<List<InventoryItem>> FetchAsync(
        SteamWebSession session,
        PriceCache? prices,
        decimal minPrice,
        bool tradableOnly,
        bool marketableOnly,
        CancellationToken ct = default)
    {
        var outItems = new List<InventoryItem>();
        string? start = null;
        var descriptions = new Dictionary<(string, string), JsonElement>();

        while (true)
        {
            var url = $"{SteamWebSession.Community}/inventory/{session.Account.Steam64Id}/{AppId}/{ContextId}?l=english&count=2000";
            if (!string.IsNullOrEmpty(start))
                url += $"&start_assetid={start}";

            using var doc = await GetInventoryPageAsync(session, url, ct).ConfigureAwait(false);
            ParseAssets(doc.RootElement, descriptions, outItems, prices, minPrice, tradableOnly, marketableOnly, session.Account.Username);
            if (!doc.RootElement.TryGetProperty("more_items", out var mi) || mi.GetInt32() == 0)
                break;
            start = doc.RootElement.TryGetProperty("last_assetid", out var la) ? la.ToString() : null;
            if (string.IsNullOrEmpty(start)) break;
        }
        return outItems;
    }

    static async Task<JsonDocument> GetInventoryPageAsync(SteamWebSession session, string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var resp = await session.Http.GetAsync(url, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code == 403)
                throw new InvalidOperationException("inventory private / forbidden (403)");
            if (code is 429 or 500 or 502 or 503 or 504)
            {
                if (attempt == 2) throw new HttpRequestException($"{code}");
                await Task.Delay(code == 429 ? 6000 : 3000, ct).ConfigureAwait(false);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonDocument.Parse(text);
        }
        throw new HttpRequestException("inventory fetch failed");
    }

    static void ParseAssets(JsonElement root, Dictionary<(string, string), JsonElement> descriptions,
        List<InventoryItem> outItems, PriceCache? prices, decimal minPrice, bool tradableOnly, bool marketableOnly,
        string username)
    {
        if (root.TryGetProperty("descriptions", out var descs) && descs.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in descs.EnumerateArray())
            {
                var classid = d.GetProperty("classid").ToString();
                var instanceid = d.GetProperty("instanceid").ToString();
                descriptions[(classid, instanceid)] = d.Clone();
            }
        }
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return;
        foreach (var a in assets.EnumerateArray())
        {
            var classid = a.GetProperty("classid").ToString();
            var instanceid = a.GetProperty("instanceid").ToString();
            if (!descriptions.TryGetValue((classid, instanceid), out var desc))
                continue;
            var tradable = desc.TryGetProperty("tradable", out var t) && t.GetInt32() == 1;
            var marketable = desc.TryGetProperty("marketable", out var m) && m.GetInt32() == 1;
            if (tradableOnly && !tradable) continue;
            if (marketableOnly && !marketable) continue;
            var name = desc.TryGetProperty("market_hash_name", out var mh) ? mh.GetString()
                : desc.TryGetProperty("market_name", out var mn) ? mn.GetString()
                : desc.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;
            decimal price = 0;
            if (prices is not null)
            {
                if (!prices.TryGet(name, out price) || price < minPrice)
                    continue;
            }
            var amount = a.TryGetProperty("amount", out var am) ? int.Parse(am.ToString()) : 1;
            outItems.Add(new InventoryItem
            {
                SourceUsername = username,
                AssetId = a.GetProperty("assetid").ToString(),
                ClassId = classid,
                InstanceId = instanceid,
                MarketHashName = name!,
                Amount = amount,
                PriceUsd = price,
                Tradable = tradable,
                Marketable = marketable,
            });
        }
    }

    public static decimal SumValue(IEnumerable<InventoryItem> items) =>
        items.Sum(i => i.PriceUsd * i.Amount);
}
