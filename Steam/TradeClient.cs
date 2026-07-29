using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeInventory.Models;

namespace TradeInventory.Steam;

public static class TradeClient
{
    const int AppId = 730;
    const string ContextId = "2";
    static readonly Regex TokenInHtml = new(
        @"https://steamcommunity\.com/tradeoffer/new/\?partner=(\d+)&token=([a-zA-Z0-9_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex TokenLoose = new(
        @"trade_offer_access_token[""'=\s:]+([a-zA-Z0-9_-]{6,})",
        RegexOptions.Compiled);

    public static async Task<string> FetchTradeTokenAsync(SteamWebSession session, CancellationToken ct = default)
    {
        var steam64 = session.Account.Steam64Id;
        // warm
        foreach (var url in new[]
                 {
                     SteamWebSession.Community + "/",
                     $"{SteamWebSession.Community}/profiles/{steam64}/",
                     $"{SteamWebSession.Community}/profiles/{steam64}/tradeoffers/",
                 })
        {
            try
            {
                using var r = await session.Http.GetAsync(url, ct).ConfigureAwait(false);
                _ = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch { /* ignore warm errors unless proxy */ }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        foreach (var url in new[]
                 {
                     $"{SteamWebSession.Community}/profiles/{steam64}/tradeoffers/privacy",
                     $"{SteamWebSession.Community}/my/tradeoffers/privacy",
                 })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri($"{SteamWebSession.Community}/profiles/{steam64}/tradeoffers/");
            using var resp = await session.Http.SendAsync(req, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (code == 429) throw new HttpRequestException("429");
            if (code is 500 or 502 or 503 or 504) throw new HttpRequestException($"HTTP {code}");
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = TokenInHtml.Match(html);
            if (m.Success) return m.Groups[2].Value;
            var m2 = TokenLoose.Match(html);
            if (m2.Success) return m2.Groups[1].Value;
        }
        throw new InvalidOperationException("trade token not found on privacy page");
    }

    public static async Task<(string OfferId, bool NeedsConfirm)> SendOfferAsync(
        SteamWebSession session,
        string partnerSteam64,
        string tradeToken,
        IReadOnlyList<InventoryItem> items,
        string message = "",
        CancellationToken ct = default)
    {
        var sessionId = session.Cookie("sessionid")
            ?? throw new InvalidOperationException("missing sessionid");

        var merged = new Dictionary<string, Dictionary<string, string>>();
        foreach (var it in items)
        {
            if (merged.TryGetValue(it.AssetId, out var slot))
                slot["amount"] = (int.Parse(slot["amount"]) + it.Amount).ToString();
            else
                merged[it.AssetId] = new Dictionary<string, string>
                {
                    ["appid"] = AppId.ToString(),
                    ["contextid"] = ContextId,
                    ["amount"] = it.Amount.ToString(),
                    ["assetid"] = it.AssetId,
                };
        }

        var payload = new
        {
            newversion = true,
            version = 4,
            me = new { assets = merged.Values.ToList(), currency = Array.Empty<object>(), ready = false },
            them = new { assets = Array.Empty<object>(), currency = Array.Empty<object>(), ready = false },
        };

        var partnerId = ulong.Parse(partnerSteam64) - 76561197960265728UL;
        var referer = $"{SteamWebSession.Community}/tradeoffer/new/?partner={partnerId}&token={tradeToken}";
        // Warm trade page (steampy make_offer_with_url does this before POST /send)
        try { _ = await session.Http.GetAsync(referer, ct).ConfigureAwait(false); } catch { /* ignore */ }
        await MobileConfirmations.AcknowledgeTradeProtectionAsync(session, ct).ConfigureAwait(false);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["sessionid"] = sessionId,
            ["serverid"] = "1",
            ["partner"] = partnerSteam64,
            ["tradeoffermessage"] = message,
            ["json_tradeoffer"] = JsonSerializer.Serialize(payload),
            ["captcha"] = "",
            ["trade_offer_create_params"] = JsonSerializer.Serialize(new { trade_offer_access_token = tradeToken }),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{SteamWebSession.Community}/tradeoffer/new/send")
        {
            Content = content,
        };
        req.Headers.Referrer = new Uri(referer);
        req.Headers.TryAddWithoutValidation("Origin", SteamWebSession.Community);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        using var resp = await session.Http.SendAsync(req, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code == 429) throw new HttpRequestException("429");
        if (code is 500 or 502 or 503 or 504) throw new HttpRequestException($"HTTP {code}");
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("tradeofferid", out var oid))
            throw new InvalidOperationException($"trade send failed: {text[..Math.Min(300, text.Length)]}");
        var needs = doc.RootElement.TryGetProperty("needs_mobile_confirmation", out var n) &&
                    (n.ValueKind == JsonValueKind.True || (n.ValueKind == JsonValueKind.Number && n.GetInt32() != 0));
        return (oid.ToString(), needs);
    }

    /// <summary>
    /// Accept inbound offer — port of trade_steam/steam_trade_ops.accept_trade_offer.
    /// Acknowledges Trade Protection first; if Steam asks for mobile confirmation on
    /// the destination, confirms with identity_secret (same as trade_item.py).
    /// </summary>
    public static async Task AcceptOfferAsync(
        SteamWebSession destSession,
        string tradeOfferId,
        string partnerSteam64,
        string? destIdentitySecret = null,
        string? destSteam64 = null,
        CancellationToken ct = default)
    {
        tradeOfferId = tradeOfferId.Trim().Trim('"');
        await MobileConfirmations.AcknowledgeTradeProtectionAsync(destSession, ct).ConfigureAwait(false);

        var sessionId = destSession.Cookie("sessionid")
            ?? throw new InvalidOperationException("missing sessionid for accept");
        var referer = $"{SteamWebSession.Community}/tradeoffer/{tradeOfferId}";
        try
        {
            using var page = await destSession.Http.GetAsync(referer + "/", ct).ConfigureAwait(false);
            var html = await page.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var low = html.ToLowerInvariant();
            if (low.Contains("no longer available") || low.Contains("больше не доступен"))
                throw new InvalidOperationException($"offer {tradeOfferId} is no longer available");
        }
        catch (InvalidOperationException) { throw; }
        catch { /* ignore warm errors */ }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["sessionid"] = sessionId,
            ["serverid"] = "1",
            ["tradeofferid"] = tradeOfferId,
            ["partner"] = partnerSteam64,
            ["captcha"] = "",
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{SteamWebSession.Community}/tradeoffer/{tradeOfferId}/accept")
        {
            Content = content,
        };
        req.Headers.Referrer = new Uri(referer);
        req.Headers.TryAddWithoutValidation("Origin", SteamWebSession.Community);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

        using var resp = await destSession.Http.SendAsync(req, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code == 429) throw new HttpRequestException("429");
        if (code is 500 or 502 or 503 or 504) throw new HttpRequestException($"HTTP {code}");
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("strError", out var err))
            throw new InvalidOperationException($"accept failed: {err.GetString()}");
        if (code >= 400)
            throw new HttpRequestException($"accept HTTP {code}: {text[..Math.Min(200, text.Length)]}");

        var needsConfirm = doc.RootElement.TryGetProperty("needs_mobile_confirmation", out var nmc) &&
                           (nmc.ValueKind == JsonValueKind.True ||
                            (nmc.ValueKind == JsonValueKind.Number && nmc.GetInt32() != 0));
        if (!needsConfirm) return;

        if (string.IsNullOrEmpty(destIdentitySecret) || string.IsNullOrEmpty(destSteam64))
            throw new InvalidOperationException("accept needs mobile confirmation on destination");

        Log.Info($"      accept needs mobile confirmation — confirming on dest");
        await MobileConfirmations.ConfirmTradeOfferAsync(
            destSession, destIdentitySecret, destSteam64, tradeOfferId,
            maxWait: TimeSpan.FromSeconds(120), ct: ct).ConfigureAwait(false);
    }

    /// <summary>Cancel all active sent trade offers (clears stale mobile confs before a new send).</summary>
    public static async Task<int> CancelActiveSentOffersAsync(SteamWebSession session, CancellationToken ct = default)
    {
        var ids = await GetActiveSentOfferIdsAsync(session, ct).ConfigureAwait(false);
        if (ids.Count == 0) return 0;
        var cancelled = 0;
        foreach (var id in ids)
        {
            try
            {
                await CancelOfferAsync(session, id, ct).ConfigureAwait(false);
                cancelled++;
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Info($"      cancel offer {id} failed: {ex.Message}");
            }
        }
        return cancelled;
    }

    public static async Task<List<string>> GetActiveSentOfferIdsAsync(SteamWebSession session, CancellationToken ct = default)
    {
        var access = ExtractAccessToken(session)
            ?? throw new InvalidOperationException("missing access token for GetTradeOffers");
        var url =
            $"{SteamWebSession.Api}/IEconService/GetTradeOffers/v1/?access_token={Uri.EscapeDataString(access)}" +
            "&get_sent_offers=1&get_received_offers=0&active_only=1&historical_only=0&get_descriptions=0";
        using var resp = await session.Http.GetAsync(url, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code == 429) throw new HttpRequestException("429");
        if (code is 500 or 502 or 503 or 504) throw new HttpRequestException($"HTTP {code}");
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var ids = new List<string>();
        if (!doc.RootElement.TryGetProperty("response", out var response))
            return ids;
        if (!response.TryGetProperty("trade_offers_sent", out var sent) || sent.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var o in sent.EnumerateArray())
        {
            // ETradeOfferState.Active = 2, NeedsConfirmation = 9
            var state = o.TryGetProperty("trade_offer_state", out var st) ? st.GetInt32() : 0;
            if (state is not (2 or 9)) continue;
            if (o.TryGetProperty("tradeofferid", out var oid))
                ids.Add(oid.ToString().Trim('"'));
        }
        return ids;
    }

    public static async Task CancelOfferAsync(SteamWebSession session, string tradeOfferId, CancellationToken ct = default)
    {
        var sessionId = session.Cookie("sessionid")
            ?? throw new InvalidOperationException("missing sessionid for cancel");
        tradeOfferId = tradeOfferId.Trim().Trim('"');

        // Prefer community cancel (same as browser / ASF web).
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["sessionid"] = sessionId,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{SteamWebSession.Community}/tradeoffer/{tradeOfferId}/cancel")
        {
            Content = content,
        };
        req.Headers.Referrer = new Uri($"{SteamWebSession.Community}/tradeoffer/{tradeOfferId}/");
        req.Headers.TryAddWithoutValidation("Origin", SteamWebSession.Community);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        using var resp = await session.Http.SendAsync(req, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code == 429) throw new HttpRequestException("429");
        if (code is 500 or 502 or 503 or 504) throw new HttpRequestException($"HTTP {code}");
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (code >= 400)
        {
            // Fallback: WebAPI CancelTradeOffer
            var access = ExtractAccessToken(session)
                ?? throw new HttpRequestException($"cancel HTTP {code}: {text[..Math.Min(160, text.Length)]}");
            using var apiContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["access_token"] = access,
                ["tradeofferid"] = tradeOfferId,
            });
            using var apiResp = await session.Http.PostAsync(
                $"{SteamWebSession.Api}/IEconService/CancelTradeOffer/v1/", apiContent, ct).ConfigureAwait(false);
            apiResp.EnsureSuccessStatusCode();
            return;
        }
        // community often returns {"tradeofferid":"..."} or success HTML/json
        if (text.Contains("strError", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"cancel failed: {text[..Math.Min(200, text.Length)]}");
    }

    static string? ExtractAccessToken(SteamWebSession session)
    {
        if (!string.IsNullOrEmpty(session.AccessToken))
            return session.AccessToken;
        var secure = session.Cookie("steamLoginSecure");
        if (string.IsNullOrEmpty(secure)) return null;
        // format: steamid||jwt
        var parts = secure.Split(["||"], 2, StringSplitOptions.None);
        return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : null;
    }

}
