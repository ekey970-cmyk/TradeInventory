using System.Globalization;
using System.Text.Json;

namespace TradeInventory.Data;

public sealed class PriceCache
{
    readonly Dictionary<string, decimal> _prices = new(StringComparer.Ordinal);

    public int Count => _prices.Count;

    public static PriceCache Load(string path)
    {
        var cache = new PriceCache();
        if (!File.Exists(path))
            throw new FileNotFoundException("price_cache.json not found", path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var key = prop.Name;
            var name = key.Contains("::", StringComparison.Ordinal)
                ? key[(key.IndexOf("::", StringComparison.Ordinal) + 2)..]
                : key;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Value.TryGetProperty("price", out var p)) continue;
            decimal price;
            if (p.ValueKind == JsonValueKind.Number)
                price = p.GetDecimal();
            else if (!decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out price))
                continue;
            cache._prices[name] = price;
        }
        return cache;
    }

    public bool TryGet(string marketHashName, out decimal price) =>
        _prices.TryGetValue(marketHashName, out price);
}
