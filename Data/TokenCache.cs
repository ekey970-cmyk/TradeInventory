using System.Text.Json;
using System.Text.RegularExpressions;
using TradeInventory.Models;

namespace TradeInventory.Data;

public sealed class TokenCache
{
    static readonly Regex UrlRe = new(
        @"https://steamcommunity\.com/tradeoffer/new/\?partner=(\d+)&token=([a-zA-Z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    readonly string _path;

    public TokenCache(string path) => _path = path;

    public static TokenCache Load(string path)
    {
        var c = new TokenCache(path);
        if (!File.Exists(path)) return c;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var tok = Normalize(p.Value.GetString());
                if (!string.IsNullOrEmpty(tok))
                    c._map[p.Name] = tok;
            }
        }
        catch { /* ignore corrupt cache */ }
        return c;
    }

    public string Resolve(SteamAccount account)
    {
        var direct = Normalize(account.TradeToken);
        if (!string.IsNullOrEmpty(direct)) return direct;
        foreach (var key in new[] { account.Steam64Id, account.Username, account.PartnerId })
        {
            if (!string.IsNullOrEmpty(key) && _map.TryGetValue(key, out var tok))
                return tok;
        }
        return "";
    }

    public void Put(SteamAccount account, string token)
    {
        token = Normalize(token);
        if (string.IsNullOrEmpty(token)) return;
        account.TradeToken = token;
        foreach (var key in new[] { account.Steam64Id, account.Username, account.PartnerId })
        {
            if (!string.IsNullOrEmpty(key))
                _map[key] = token;
        }
    }

    public void Save()
    {
        var ordered = _map.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
        File.WriteAllText(_path, JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    public static string Normalize(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return "";
        var m = UrlRe.Match(raw);
        if (m.Success) return m.Groups[2].Value;
        return Regex.IsMatch(raw, @"^[A-Za-z0-9_-]{6,}$") ? raw : "";
    }
}
