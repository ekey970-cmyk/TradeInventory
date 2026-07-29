namespace TradeInventory.Models;

public sealed class SteamAccount
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string SharedSecret { get; init; } = "";
    public string IdentitySecret { get; init; } = "";
    public string Steam64Id { get; init; } = "";
    public string PartnerId { get; init; } = "";
    public string TradeToken { get; set; } = "";
    public string? TradeDate { get; set; }

    public ulong SteamId64 => ulong.TryParse(Steam64Id, out var id) ? id : 0UL;

    public uint AccountId
    {
        get
        {
            if (uint.TryParse(PartnerId, out var id)) return id;
            return SteamId64 > 0 ? (uint)(SteamId64 - 76561197960265728UL) : 0u;
        }
    }
}
