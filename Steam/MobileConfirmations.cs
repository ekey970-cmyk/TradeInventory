using System.Globalization;
using System.Net;
using System.Text.Json;

namespace TradeInventory.Steam;

/// <summary>
/// Mobile trade confirmations — port of trade_steam/steam_trade_ops.py
/// (used by the working trade_item.py).
///
/// Critical: Steam Trade Protection must be acknowledged first, otherwise
/// ajaxop always returns success=false.
///
/// Flow matches steampy/ASF web:
///   getlist + ajaxop both use m=react, tag=conf, HMAC tag "conf"
///   device id = steampy guard.generate_device_id (sha1(steam64))
/// </summary>
public static class MobileConfirmations
{
    public static async Task AcknowledgeTradeProtectionAsync(
        SteamWebSession session, CancellationToken ct = default)
    {
        var sessionId = session.Cookie("sessionid")
            ?? throw new InvalidOperationException("missing sessionid for trade acknowledge");

        foreach (var url in new[]
                 {
                     $"{SteamWebSession.Community}/trade/new/acknowledge",
                     $"{SteamWebSession.Community}//trade/new/acknowledge",
                 })
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["sessionid"] = sessionId,
                    ["message"] = "1",
                });
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.Referrer = new Uri($"{SteamWebSession.Community}/mobileconf/conf");
                req.Headers.TryAddWithoutValidation("Origin", SteamWebSession.Community);
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                using var resp = await session.Http.SendAsync(req, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log.Info($"      trade acknowledge -> HTTP {(int)resp.StatusCode} {Trim(text, 80)}");
                if (resp.IsSuccessStatusCode) return;
            }
            catch (Exception ex)
            {
                Log.Info($"      acknowledge fail {url}: {ex.Message}");
            }
        }
    }

    public static async Task ConfirmTradeOfferAsync(
        SteamWebSession session,
        string identitySecret,
        string steam64,
        string tradeOfferId,
        TimeSpan? maxWait = null,
        CancellationToken ct = default)
    {
        tradeOfferId = NormalizeId(tradeOfferId);

        try { await session.EnsureLoggedInAsync(force: false, ct).ConfigureAwait(false); }
        catch { /* best-effort */ }

        // steampy generate_device_id(steam64) — sha1(steam64), not ASF android: prefix hash
        var deviceId = SteamTotpDeviceId(steam64);
        Log.Info($"      confirm device_id={Trim(deviceId, 48)} via={session.Proxy?.Tag ?? "direct"}");

        await AcknowledgeTradeProtectionAsync(session, ct).ConfigureAwait(false);
        await Task.Delay(800, ct).ConfigureAwait(false);

        var wait = maxWait ?? TimeSpan.FromSeconds(180);
        var deadline = DateTimeOffset.UtcNow + wait;
        var attempt = 0;
        Exception? last = null;
        var lastCount = -1;
        var lastCreators = "";

        while (DateTimeOffset.UtcNow < deadline)
        {
            attempt++;
            try
            {
                await ConfirmationsLimiter.WaitAsync(ct).ConfigureAwait(false);
                var confs = await FetchConfirmationsAsync(session, identitySecret, steam64, deviceId, ct)
                    .ConfigureAwait(false);
                lastCount = confs.Count;
                lastCreators = string.Join(',', confs.Select(c => $"{c.CreatorId}:{c.Type}"));

                var target = confs.FirstOrDefault(c => IdsEqual(c.CreatorId, tradeOfferId));
                if (target is null)
                {
                    // steam_trade_ops: empty list after a couple tries → already confirmed
                    if (attempt >= 2 && confs.Count == 0)
                    {
                        Log.Info($"      confirm offer {tradeOfferId}: already confirmed (empty list)");
                        return;
                    }
                    if (attempt == 1 || attempt % 3 == 0)
                        Log.Info($"      confirm waiting offer={tradeOfferId} confs={lastCount} creators=[{lastCreators}]");
                }
                else
                {
                    await ConfirmationsLimiter.WaitAsync(ct).ConfigureAwait(false);
                    var (ok, body) = await AllowConfirmationAsync(
                        session, identitySecret, steam64, deviceId, target, ct).ConfigureAwait(false);
                    if (ok)
                    {
                        Log.Info($"      confirm ajaxop ok cid={target.Id}");
                        return;
                    }

                    Log.Info($"      confirm ajaxop false cid={target.Id}: {Trim(body, 200)}");
                    // steam_trade_ops: re-ack then retry
                    await AcknowledgeTradeProtectionAsync(session, ct).ConfigureAwait(false);

                    await Task.Delay(500, ct).ConfigureAwait(false);
                    await ConfirmationsLimiter.WaitAsync(ct).ConfigureAwait(false);
                    var again = await FetchConfirmationsAsync(session, identitySecret, steam64, deviceId, ct)
                        .ConfigureAwait(false);
                    if (again.All(c => !IdsEqual(c.CreatorId, tradeOfferId)))
                    {
                        Log.Info($"      confirm conf gone after allow — treating as confirmed");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                last = ex;
                if (ex.Message.Contains("needauth", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("Needs Authentication", StringComparison.OrdinalIgnoreCase))
                    throw;

                if (ex.ToString().Contains("429", StringComparison.Ordinal))
                {
                    var backoff = TimeSpan.FromSeconds(Math.Min(20 + attempt * 5, 60));
                    Log.Info($"      confirm 429; wait {backoff.TotalSeconds:0}s");
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    continue;
                }
                Log.Info($"      confirm poll error: {Short(ex.Message)}");
            }

            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            last is null
                ? $"confirmation missing for offer {tradeOfferId} (polled {attempt}x, last_confs={lastCount}, creators=[{lastCreators}])"
                : $"confirm timeout for offer {tradeOfferId}: {last.Message}");
    }

    /// <summary>steam_trade_ops list_confirmations — tag/HMAC = conf</summary>
    static async Task<List<Confirmation>> FetchConfirmationsAsync(
        SteamWebSession session, string identitySecret, string steam64, string deviceId, CancellationToken ct)
    {
        var time = await SteamTime.NowAsync(session, session.Http, ct).ConfigureAwait(false);
        var hash = SteamGuard.ConfirmationHash(identitySecret, time, "conf");
        var url =
            $"{SteamWebSession.Community}/mobileconf/getlist" +
            $"?p={Uri.EscapeDataString(deviceId)}" +
            $"&a={Uri.EscapeDataString(steam64)}" +
            $"&k={Uri.EscapeDataString(hash)}" +
            $"&t={time}&m=react&tag=conf";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri($"{SteamWebSession.Community}/mobileconf/conf");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "com.valvesoftware.android.steam.community");
        using var resp = await session.Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureHttpOk(resp, text);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("needauth", out var na) && IsTruthy(na))
            throw new InvalidOperationException("Needs Authentication");
        if (!root.TryGetProperty("success", out var ok) || !IsTruthy(ok))
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : text;
            throw new InvalidOperationException(msg ?? "getlist failed");
        }

        var list = new List<Confirmation>();
        if (!root.TryGetProperty("conf", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var c in arr.EnumerateArray())
        {
            var id = ReadId(c, "id");
            var nonce = c.TryGetProperty("nonce", out _) ? ReadId(c, "nonce") : ReadId(c, "key");
            var creator = c.TryGetProperty("creator_id", out _) ? ReadId(c, "creator_id") : "";
            var type = c.TryGetProperty("type", out _) ? ReadId(c, "type") : "";
            if (id.Length == 0 || nonce.Length == 0) continue;
            list.Add(new Confirmation(id, nonce, creator, type));
        }
        return list;
    }

    /// <summary>steam_trade_ops ajaxop allow — tag/HMAC = conf</summary>
    static async Task<(bool Ok, string Body)> AllowConfirmationAsync(
        SteamWebSession session, string identitySecret, string steam64, string deviceId,
        Confirmation conf, CancellationToken ct)
    {
        var time = await SteamTime.NowAsync(session, session.Http, ct).ConfigureAwait(false);
        var hash = SteamGuard.ConfirmationHash(identitySecret, time, "conf");
        var url =
            $"{SteamWebSession.Community}/mobileconf/ajaxop" +
            $"?op=allow" +
            $"&p={Uri.EscapeDataString(deviceId)}" +
            $"&a={Uri.EscapeDataString(steam64)}" +
            $"&k={Uri.EscapeDataString(hash)}" +
            $"&t={time}&m=react&tag=conf" +
            $"&cid={Uri.EscapeDataString(conf.Id)}" +
            $"&ck={Uri.EscapeDataString(conf.Nonce)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri($"{SteamWebSession.Community}/mobileconf/conf");
        req.Headers.TryAddWithoutValidation("Origin", SteamWebSession.Community);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        using var resp = await session.Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureHttpOk(resp, text);

        using var doc = JsonDocument.Parse(text);
        if ((doc.RootElement.TryGetProperty("needsauth", out var na) && IsTruthy(na)) ||
            (doc.RootElement.TryGetProperty("needauth", out var na2) && IsTruthy(na2)))
            throw new InvalidOperationException($"confirm needsauth: {Trim(text, 200)}");

        var ok = doc.RootElement.TryGetProperty("success", out var s) && IsTruthy(s);
        return (ok, text);
    }

    /// <summary>steampy guard.generate_device_id — sha1(steam64).</summary>
    public static string SteamTotpDeviceId(string steam64)
    {
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(steam64))).ToLowerInvariant();
        return $"android:{digest[..8]}-{digest[8..12]}-{digest[12..16]}-{digest[16..20]}-{digest[20..32]}";
    }

    static void EnsureHttpOk(HttpResponseMessage resp, string text)
    {
        var code = (int)resp.StatusCode;
        if (code == 429) throw new HttpRequestException("429");
        if (code is 500 or 502 or 503 or 504) throw new HttpRequestException($"HTTP {code}");
        if (code >= 400) throw new HttpRequestException($"HTTP {code}: {Trim(text, 160)}");
    }

    static string ReadId(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return "";
        return el.ValueKind switch
        {
            JsonValueKind.String => NormalizeId(el.GetString() ?? ""),
            JsonValueKind.Number => NormalizeId(el.GetRawText()),
            _ => NormalizeId(el.ToString()),
        };
    }

    static string NormalizeId(string s) => (s ?? "").Trim().Trim('"');

    static bool IdsEqual(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        return ulong.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
               && ulong.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
               && x == y;
    }

    static bool IsTruthy(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.TryGetInt64(out var n) ? n != 0 : el.GetDouble() != 0,
            JsonValueKind.String => el.GetString() is "1" or "true" or "True",
            _ => false
        };

    static string Short(string s) => s.Length <= 120 ? s : s[..117] + "...";
    static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    sealed record Confirmation(string Id, string Nonce, string CreatorId, string Type);
}
