namespace TradeInventory.Models;

public sealed class InventoryItem
{
    public required string SourceUsername { get; init; }
    public required string AssetId { get; init; }
    public required string ClassId { get; init; }
    public required string InstanceId { get; init; }
    public required string MarketHashName { get; init; }
    public int Amount { get; init; } = 1;
    public decimal PriceUsd { get; set; }
    public bool Tradable { get; init; }
    public bool Marketable { get; init; }
}

public sealed class DestPack
{
    public required string DestUsername { get; init; }
    public decimal ExistingValue { get; init; }
    public decimal Need { get; init; }
    public List<InventoryItem> Items { get; } = new();

    public decimal SentValue => Items.Sum(i => i.PriceUsd * i.Amount);
    public decimal ProjectedTotal => ExistingValue + SentValue;

    public Dictionary<string, List<InventoryItem>> BySource() =>
        Items.GroupBy(i => i.SourceUsername)
            .ToDictionary(g => g.Key, g => g.ToList());
}
