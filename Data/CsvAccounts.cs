using System.Globalization;
using System.Text;
using TradeInventory.Models;

namespace TradeInventory.Data;

public static class CsvAccounts
{
    static readonly string[] BaseFields =
    [
        "steam_username", "steam_password", "steam_shared_secret",
        "steam_identity_secret", "steam64_id", "steam_partner_id"
    ];

    static readonly string[] DestFields = BaseFields.Concat(["trade_token"]).ToArray();
    static readonly string[] OutFields = DestFields.Concat(["trade_date"]).ToArray();

    public static List<SteamAccount> Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Accounts CSV not found", path);

        var lines = File.ReadAllLines(path, Encoding.UTF8)
            .Select(l => l.Trim().TrimStart('\uFEFF'))
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count < 2)
            throw new InvalidOperationException($"No accounts in {path}");

        var headers = SplitCsv(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var map = headers.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i);
        string Get(string[] cols, string key) =>
            map.TryGetValue(key, out var i) && i < cols.Length ? cols[i].Trim() : "";

        var rows = new List<SteamAccount>();
        for (var n = 1; n < lines.Count; n++)
        {
            var cols = SplitCsv(lines[n]);
            var acc = new SteamAccount
            {
                Username = Get(cols, "steam_username"),
                Password = Get(cols, "steam_password"),
                SharedSecret = Get(cols, "steam_shared_secret"),
                IdentitySecret = Get(cols, "steam_identity_secret"),
                Steam64Id = Get(cols, "steam64_id"),
                PartnerId = Get(cols, "steam_partner_id"),
                TradeToken = Get(cols, "trade_token"),
                TradeDate = string.IsNullOrWhiteSpace(Get(cols, "trade_date"))
                    ? null : Get(cols, "trade_date"),
            };
            if (!string.IsNullOrWhiteSpace(acc.Username))
                rows.Add(acc);
        }
        return rows;
    }

    public static void SaveDestinations(string path, IEnumerable<SteamAccount> accounts)
    {
        Write(path, accounts, DestFields, a => new[]
        {
            a.Username, a.Password, a.SharedSecret, a.IdentitySecret,
            a.Steam64Id, a.PartnerId, a.TradeToken
        });
    }

    public static void AppendSuccess(string destPath, string outPath, SteamAccount account, string tradeDate)
    {
        account.TradeDate = tradeDate;
        var outs = File.Exists(outPath) ? Load(outPath) : new List<SteamAccount>();
        var existing = outs.FirstOrDefault(a => a.Username == account.Username);
        if (existing is null)
            outs.Add(account);
        else
        {
            existing.TradeToken = account.TradeToken;
            existing.TradeDate = tradeDate;
        }
        Write(outPath, outs, OutFields, a => new[]
        {
            a.Username, a.Password, a.SharedSecret, a.IdentitySecret,
            a.Steam64Id, a.PartnerId, a.TradeToken, a.TradeDate ?? ""
        });

        var dests = Load(destPath).Where(a => a.Username != account.Username).ToList();
        SaveDestinations(destPath, dests);
    }

    static void Write(string path, IEnumerable<SteamAccount> accounts, string[] fields, Func<SteamAccount, string[]> row)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', fields));
        foreach (var a in accounts)
            sb.AppendLine(string.Join(',', row(a).Select(Escape)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    static string Escape(string v)
    {
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            return '"' + v.Replace("\"", "\"\"") + '"';
        return v;
    }

    static string[] SplitCsv(string line)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        var inQ = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQ)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                { cur.Append('"'); i++; }
                else if (c == '"') inQ = false;
                else cur.Append(c);
            }
            else
            {
                if (c == '"') inQ = true;
                else if (c == ',') { list.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
        }
        list.Add(cur.ToString());
        return list.ToArray();
    }
}
